using System.Collections.Concurrent;
using System.Diagnostics;
using VerbaFlow.Core.Domain;
using VerbaFlow.Core.Providers;

namespace VerbaFlow.Infrastructure.StandIns;

/// <summary>
/// Stand-in for the note-taker service: nobody joins any meeting. Each time it is asked, a booked note-taker moves one
/// step on (joining, recording, meeting ended, ready), and the "recording" is a short sample made here: a test picture
/// with a tone (an MP4 when ffmpeg is installed, otherwise sound only), with two made-up speakers taking turns.
/// </summary>
public sealed class StandInNoteTaker(TimeProvider clock, string? ffmpegPath = null) : INoteTakerService
{
    public const string EngineName = "stand-in-note-taker";
    public const int SampleMs = 12_000;
    public string Name => EngineName;

    private sealed class Bot(NoteTakerRequest request)
    {
        public NoteTakerRequest Request { get; } = request;
        public int Steps;
        public bool Cancelled;
    }

    private readonly ConcurrentDictionary<string, Bot> bots = new();

    public Task<string> ScheduleAsync(NoteTakerRequest request, CancellationToken ct)
    {
        var id = "standin-" + Guid.NewGuid().ToString("N")[..12];
        bots[id] = new Bot(request);
        return Task.FromResult(id);
    }

    public Task<NoteTakerStatus> GetAsync(string botId, CancellationToken ct)
    {
        if (!bots.TryGetValue(botId, out var bot)) throw new ProviderException(FaultKind.Other, $"Stand-in note-taker {botId} is not known (the app was restarted).");
        if (bot.Cancelled) return Task.FromResult(new NoteTakerStatus(NoteTakerPhase.Failed, "cancelled"));
        if (bot.Request.JoinAt is { } at && at > clock.GetUtcNow()) return Task.FromResult(new NoteTakerStatus(NoteTakerPhase.Scheduled));
        var step = Interlocked.Increment(ref bot.Steps);
        return Task.FromResult(step switch
        {
            1 => new NoteTakerStatus(NoteTakerPhase.Joining),
            2 => new NoteTakerStatus(NoteTakerPhase.Recording),
            3 => new NoteTakerStatus(NoteTakerPhase.MeetingEnded),
            _ => new NoteTakerStatus(NoteTakerPhase.Ready, null, $"standin://{botId}/video", $"standin://{botId}/speakers"),
        });
    }

    public Task CancelAsync(string botId, CancellationToken ct)
    {
        if (bots.TryGetValue(botId, out var bot)) bot.Cancelled = true;
        return Task.CompletedTask;
    }

    public Task ForgetAsync(string botId, CancellationToken ct)
    {
        bots.TryRemove(botId, out _);
        return Task.CompletedTask;
    }

    /// <summary>Whether the sample is a video (ffmpeg found) or sound only.</summary>
    public bool MakesVideo => FfmpegWorks(ffmpegPath);

    public async Task<Stream> DownloadVideoAsync(string url, CancellationToken ct)
    {
        if (MakesVideo)
        {
            var file = Path.Combine(Path.GetTempPath(), $"verbaflow-standin-{Guid.NewGuid():N}.mp4");
            var psi = new ProcessStartInfo(Exe(ffmpegPath)) { RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
            foreach (var a in new[] { "-hide_banner", "-loglevel", "error", "-y", "-f", "lavfi", "-i", $"testsrc=size=320x180:rate=10:duration={SampleMs / 1000}",
                "-f", "lavfi", "-i", $"sine=frequency=330:duration={SampleMs / 1000}", "-c:v", "libx264", "-pix_fmt", "yuv420p", "-preset", "ultrafast",
                "-c:a", "aac", "-shortest", "-movflags", "+faststart", file })
                psi.ArgumentList.Add(a);
            using var p = Process.Start(psi)!;
            var err = p.StandardError.ReadToEndAsync(ct);
            await p.WaitForExitAsync(ct);
            if (p.ExitCode == 0 && File.Exists(file))
                return new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.DeleteOnClose);
            throw new InvalidOperationException("ffmpeg could not make the stand-in video: " + await err);
        }
        return new MemoryStream(ToneWav(SampleMs));
    }

    public Task<IReadOnlyList<PlatformSpeech>> DownloadSpeakersAsync(string url, CancellationToken ct)
    {
        // Two made-up people taking turns every 4 seconds, like the stand-in transcript's passages.
        var list = new List<PlatformSpeech>();
        for (var i = 0; i * 4000 < SampleMs; i++)
            list.Add(new PlatformSpeech(i * 4000, Math.Min(SampleMs, i * 4000 + 3900), i % 2 == 0 ? 100 : 200, i % 2 == 0 ? "Pat Stand-in" : "Lee Stand-in"));
        return Task.FromResult<IReadOnlyList<PlatformSpeech>>(list);
    }

    private static string Exe(string? path) => string.IsNullOrWhiteSpace(path) ? "ffmpeg" : path;

    private static bool? works;
    private static bool FfmpegWorks(string? path)
    {
        if (works is { } w) return w;
        try
        {
            using var p = Process.Start(new ProcessStartInfo(Exe(path), "-version") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false });
            p!.WaitForExit(5000);
            works = p.ExitCode == 0;
        }
        catch { works = false; }
        return works.Value;
    }

    /// <summary>A 16 kHz mono WAV with a quiet tone.</summary>
    public static byte[] ToneWav(int ms)
    {
        const int rate = 16000;
        var samples = rate * ms / 1000;
        using var m = new MemoryStream();
        using var w = new BinaryWriter(m);
        w.Write("RIFF"u8); w.Write(36 + samples * 2); w.Write("WAVE"u8);
        w.Write("fmt "u8); w.Write(16); w.Write((short)1); w.Write((short)1); w.Write(rate); w.Write(rate * 2); w.Write((short)2); w.Write((short)16);
        w.Write("data"u8); w.Write(samples * 2);
        for (var i = 0; i < samples; i++) w.Write((short)(Math.Sin(2 * Math.PI * 330 * i / rate) * 3000));
        w.Flush();
        return m.ToArray();
    }
}
