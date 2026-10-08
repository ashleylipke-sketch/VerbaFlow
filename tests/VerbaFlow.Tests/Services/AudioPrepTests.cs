using System.Diagnostics;
using VerbaFlow.Infrastructure.Services;

namespace VerbaFlow.Tests.Services;

public class AudioPrepTests
{
    private static string Run(string exe, params string[] args)
    {
        var psi = new System.Diagnostics.ProcessStartInfo(exe) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var o = p.StandardOutput.ReadToEnd(); p.WaitForExit();
        return o.Trim();
    }

    /// <summary>A stereo 44.1 kHz clip with a loud voice then a very quiet one.</summary>
    private static byte[] Clip()
    {
        var path = Path.Combine(Path.GetTempPath(), $"vf-clip-{Guid.NewGuid():N}.wav");
        Run("ffmpeg", "-loglevel", "error", "-y", "-f", "lavfi", "-i", "sine=f=300:d=2,volume=0.8", "-f", "lavfi", "-i", "sine=f=500:d=2,volume=0.01",
            "-filter_complex", "[0][1]concat=n=2:v=0:a=1,aformat=channel_layouts=stereo", "-ar", "44100", path);
        try { return File.ReadAllBytes(path); } finally { File.Delete(path); }
    }

    private static async Task<bool> HaveFfmpeg() => await FfmpegAudioEnhancer.IsAvailableAsync(null);

    [Fact]
    public async Task Produces_mono_16k_audio_and_lifts_the_quiet_speaker_without_touching_the_original()
    {
        if (!OperatingSystem.IsLinux() || !await HaveFfmpeg()) return; // needs ffmpeg, ffprobe and bash
        var original = Clip();
        var input = new MemoryStream(original);
        var enhanced = await new FfmpegAudioEnhancer(null).EnhanceAsync(input, default);
        Assert.Null(enhanced.Warning);
        var tmp = Path.GetTempFileName();
        await using (var f = File.Create(tmp)) { await enhanced.Audio.CopyToAsync(f); }
        await enhanced.Audio.DisposeAsync();
        try
        {
            var info = Run("ffprobe", "-v", "error", "-select_streams", "a:0", "-show_entries", "stream=channels,sample_rate,codec_name", "-of", "csv=p=0", tmp);
            Assert.Equal("flac,16000,1", info);
            // Level of the quiet half: mean volume in dB. Before it is about -60 dB; after levelling it must be clearly higher.
            double Quiet(string file) => double.Parse(Run("bash", "-c", $"ffmpeg -hide_banner -ss 2.2 -t 1.5 -i '{file}' -af volumedetect -f null - 2>&1 | grep mean_volume | sed 's/.*mean_volume: //; s/ dB//'"), System.Globalization.CultureInfo.InvariantCulture);
            var before = Path.GetTempFileName() + ".wav"; File.WriteAllBytes(before, original);
            try { Assert.True(Quiet(tmp) > Quiet(before) + 10, "the quiet speaker should be at least 10 dB louder"); } finally { File.Delete(before); }
        }
        finally { File.Delete(tmp); }
        Assert.Equal(original, input.ToArray()); // the original stream content is unchanged
    }

    [Fact]
    public async Task Cleans_up_its_temporary_folder_when_the_copy_is_closed()
    {
        if (!await HaveFfmpeg()) return;
        var before = Directory.GetDirectories(Path.GetTempPath(), "verbaflow-audio-*").Length;
        var enhanced = await new FfmpegAudioEnhancer(null).EnhanceAsync(new MemoryStream(Clip()), default);
        await enhanced.Audio.DisposeAsync();
        Assert.Equal(before, Directory.GetDirectories(Path.GetTempPath(), "verbaflow-audio-*").Length);
    }

    [Fact]
    public async Task Missing_ffmpeg_falls_back_to_the_original_audio_with_a_warning()
    {
        var bytes = new byte[] { 1, 2, 3 };
        var enhanced = await new FfmpegAudioEnhancer("/no/such/ffmpeg").EnhanceAsync(new MemoryStream(bytes), default);
        Assert.Contains("not installed", enhanced.Warning);
        Assert.Equal("none", enhanced.Applied);
        using var ms = new MemoryStream(); await enhanced.Audio.CopyToAsync(ms);
        Assert.Equal(bytes, ms.ToArray());
    }

    [Fact]
    public async Task Audio_ffmpeg_cannot_read_falls_back_with_a_warning_instead_of_failing()
    {
        if (!await HaveFfmpeg()) return;
        var enhanced = await new FfmpegAudioEnhancer(null).EnhanceAsync(new MemoryStream("pretend this is audio"u8.ToArray()), default);
        Assert.NotNull(enhanced.Warning);
        Assert.Equal("none", enhanced.Applied);
    }
}
