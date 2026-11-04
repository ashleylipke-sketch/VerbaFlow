using System.Diagnostics;
using VerbaFlow.Core.Providers;

namespace VerbaFlow.Infrastructure.Services;

/// <summary>
/// Prepares the processing copy for speaker separation: mono, 16 kHz, low rumble removed, and loudness evened out
/// so quiet speakers are not drowned out by loud ones. The original recording is never touched.
/// When asked, background noise is taken away first (RNNoise, a small speech-trained model built into ffmpeg's arnndn
/// filter; the model file ships inside VerbaFlow). Over-filtering can cost quiet words, so it is the owner's choice per run.
/// If ffmpeg is missing or fails, the original audio is used and the result carries a warning. Transcription never fails because of this step.
/// </summary>
public sealed class FfmpegAudioEnhancer(string? ffmpegPath) : IAudioEnhancer
{
    public string Name => "ffmpeg:mono-16k-levelled";
    public string Executable { get; } = string.IsNullOrWhiteSpace(ffmpegPath) ? "ffmpeg" : ffmpegPath!;

    /// <summary>The filter chain. dynaudnorm raises quiet passages gently over short windows; highpass removes room rumble below 80 Hz.</summary>
    internal static readonly string[] Arguments =
        ["-hide_banner", "-loglevel", "error", "-y", "-i", "{in}", "-vn", "-ac", "1", "-ar", "16000",
         "-af", "{filters}", "-c:a", "flac", "{out}"];

    internal const string Levelling = "highpass=f=80,dynaudnorm=f=200:g=15:p=0.9:m=15";

    /// <summary>The model file inside the temporary folder (a plain name, so no path needs escaping inside the filter, also on Windows).</summary>
    internal const string ModelFile = "rnnoise.rnnn";
    internal const string ModelName = "RNNoise somnolent-hogwash 2018-09-01";

    /// <summary>The filter chain. Noise goes first, before levelling, so the levelling does not raise the noise too.
    /// Light keeps 40% of the untouched sound mixed back in, which protects quiet words; strong keeps none.</summary>
    internal static string Filters(NoiseReduction noise) => noise switch
    {
        NoiseReduction.Light => $"highpass=f=80,arnndn=m={ModelFile}:mix=0.6,{Levelling[(Levelling.IndexOf(',') + 1)..]}",
        NoiseReduction.Strong => $"highpass=f=80,arnndn=m={ModelFile}:mix=1,{Levelling[(Levelling.IndexOf(',') + 1)..]}",
        _ => Levelling,
    };

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

    public Task<EnhancedAudio> EnhanceAsync(Stream original, CancellationToken ct) => EnhanceAsync(original, NoiseReduction.None, ct);

    public async Task<EnhancedAudio> EnhanceAsync(Stream original, NoiseReduction noise, CancellationToken ct)
    {
        var dir = Directory.CreateTempSubdirectory("verbaflow-audio-");
        var input = Path.Combine(dir.FullName, "in");
        var output = Path.Combine(dir.FullName, "out.flac");
        try
        {
            if (original.CanSeek) original.Position = 0;
            await using (var f = File.Create(input)) await original.CopyToAsync(f, ct);

            if (noise != NoiseReduction.None)
            {
                await using var model = typeof(FfmpegAudioEnhancer).Assembly.GetManifestResourceStream("rnnoise-speech.rnnn")
                    ?? throw new InvalidOperationException("The noise model is missing from this build.");
                await using var mf = File.Create(Path.Combine(dir.FullName, ModelFile));
                await model.CopyToAsync(mf, ct);
            }
            var psi = new ProcessStartInfo(Executable)
            { RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = dir.FullName };
            foreach (var a in Arguments) psi.ArgumentList.Add(a.Replace("{in}", input).Replace("{out}", output).Replace("{filters}", Filters(noise)));
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
            var applied = noise switch
            {
                NoiseReduction.Light => $"mono 16 kHz, background noise reduced a little ({ModelName}, mix 0.6), levelled",
                NoiseReduction.Strong => $"mono 16 kHz, background noise reduced a lot ({ModelName}), levelled",
                _ => "mono 16 kHz, levelled",
            };
            return new EnhancedAudio(stream, applied);
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
