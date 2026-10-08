using System.Diagnostics;
using VerbaFlow.Core.Providers;

namespace VerbaFlow.Infrastructure.Services;

/// <summary>
/// Prepares the processing copy for speaker separation: mono, 16 kHz, low rumble removed, and loudness evened out
/// so quiet speakers are not drowned out by loud ones. The original recording is never touched.
/// If ffmpeg is missing or fails, the original audio is used and the result carries a warning. Transcription never fails because of this step.
/// </summary>
public sealed class FfmpegAudioEnhancer(string? ffmpegPath) : IAudioEnhancer
{
    public string Name => "ffmpeg:mono-16k-levelled";
    public string Executable { get; } = string.IsNullOrWhiteSpace(ffmpegPath) ? "ffmpeg" : ffmpegPath!;

    /// <summary>The filter chain. dynaudnorm raises quiet passages gently over short windows; highpass removes room rumble below 80 Hz.</summary>
    internal static readonly string[] Arguments =
        ["-hide_banner", "-loglevel", "error", "-y", "-i", "{in}", "-vn", "-ac", "1", "-ar", "16000",
         "-af", "highpass=f=80,dynaudnorm=f=200:g=15:p=0.9:m=15", "-c:a", "flac", "{out}"];

    public static async Task<bool> IsAvailableAsync(string? path)
    {
        try
        {
            var psi = new ProcessStartInfo(string.IsNullOrWhiteSpace(path) ? "ffmpeg" : path, "-version")
            { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
            using var p = Process.Start(psi);
            if (p is null) return false;
            await p.WaitForExitAsync(new CancellationTokenSource(TimeSpan.FromSeconds(10)).Token);
            return p.ExitCode == 0;
        }
        catch { return false; }
    }

    public async Task<EnhancedAudio> EnhanceAsync(Stream original, CancellationToken ct)
    {
        var dir = Directory.CreateTempSubdirectory("verbaflow-audio-");
        var input = Path.Combine(dir.FullName, "in");
        var output = Path.Combine(dir.FullName, "out.flac");
        try
        {
            if (original.CanSeek) original.Position = 0;
            await using (var f = File.Create(input)) await original.CopyToAsync(f, ct);

            var psi = new ProcessStartInfo(Executable) { RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
            foreach (var a in Arguments) psi.ArgumentList.Add(a.Replace("{in}", input).Replace("{out}", output));
            using var proc = Process.Start(psi) ?? throw new InvalidOperationException("ffmpeg did not start.");
            var err = proc.StandardError.ReadToEndAsync(ct);
            _ = proc.StandardOutput.ReadToEndAsync(ct);
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
            limit.CancelAfter(TimeSpan.FromMinutes(20));
            try { await proc.WaitForExitAsync(limit.Token); }
            catch (OperationCanceledException) { try { proc.Kill(true); } catch { /* already gone */ } throw; }
            if (proc.ExitCode != 0 || !File.Exists(output) || new FileInfo(output).Length == 0)
                throw new InvalidOperationException("ffmpeg could not read this audio. " + Trim(await err));

            File.Delete(input);
            var stream = new TempFolderStream(output, dir.FullName);
            return new EnhancedAudio(stream, "mono 16 kHz, levelled");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            try { dir.Delete(true); } catch { /* best effort */ }
            if (original.CanSeek) original.Position = 0;
            var why = ex is System.ComponentModel.Win32Exception ? "ffmpeg is not installed" : ex.Message;
            return new EnhancedAudio(original, "none", "Audio was not cleaned up before transcription: " + why);
        }
    }

    private static string Trim(string s) => s.Length > 300 ? s[..300] : s.Trim();

    /// <summary>A read stream over the prepared copy that removes its temporary folder when closed.</summary>
    private sealed class TempFolderStream(string path, string folder)
        : FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous)
    {
        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            try { Directory.Delete(folder, true); } catch { /* temp folder; the OS cleans up eventually */ }
        }
    }
}
