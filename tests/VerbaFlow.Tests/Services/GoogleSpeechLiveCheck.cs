using System.Text;
using System.Text.RegularExpressions;
using VerbaFlow.Core.Providers;
using VerbaFlow.Infrastructure.Google;
using Xunit.Abstractions;

namespace VerbaFlow.Tests.Services;

/// <summary>
/// Sends real recordings to Google, only when the GitHub workflow "Google speech check" provides a token and the clips
/// (GOOGLE_SPEECH_CHECK_TOKEN, GOOGLE_SPEECH_CHECK_PROJECT, GOOGLE_SPEECH_CHECK_DIR). Everywhere else it does nothing.
/// Each clip is a minute or more of native speakers' read speech with the correct text beside it, so the result shows
/// whether the pieces, word timings and accuracy hold up through VerbaFlow's own code.
/// </summary>
public class GoogleSpeechLiveCheck(ITestOutputHelper output)
{
    [Fact]
    public async Task Real_recordings_through_Googles_EU_servers()
    {
        var token = Environment.GetEnvironmentVariable("GOOGLE_SPEECH_CHECK_TOKEN");
        var project = Environment.GetEnvironmentVariable("GOOGLE_SPEECH_CHECK_PROJECT");
        var dir = Environment.GetEnvironmentVariable("GOOGLE_SPEECH_CHECK_DIR");
        if (string.IsNullOrEmpty(token) || string.IsNullOrEmpty(project) || string.IsNullOrEmpty(dir)) return;

        var summary = new StringBuilder("| Language | Model | Length | Pieces | Passages | With word timings | Words wrong |\n|---|---|---|---|---|---|---|\n");
        foreach (var wav in Directory.GetFiles(dir, "*.wav").Order())
        {
            var code = Path.GetFileNameWithoutExtension(wav);
            var correct = await File.ReadAllTextAsync(Path.ChangeExtension(wav, ".txt"));
            var service = new GoogleSpeechService(new HttpClient { Timeout = TimeSpan.FromMinutes(5) },
                new GoogleSpeechOptions(project, "eu", GoogleCredentials.FromAccessToken(token)), null);
            await using var audio = File.OpenRead(wav);
            var r = await service.TranscribeAsync(audio, new TranscribeOptions([code], 1, false), default);
            var heard = string.Join(" ", r.Segments.Select(s => s.Text));
            var wer = WordErrorRate(correct, heard);
            var lastEnd = r.Segments.Count == 0 ? 0 : r.Segments.Max(s => s.EndMs);
            output.WriteLine($"{code}: {r.Engine}, {r.Detail}, last passage ends {lastEnd} ms, WER {wer:P0}");
            foreach (var s in r.Segments.Take(4)) output.WriteLine($"  {s.StartMs}-{s.EndMs} {s.Text} ({s.Words?.Count ?? 0} timed words)");
            var length = new FileInfo(wav).Length / 32000.0;
            summary.Append($"| {code} | {r.Engine} | {length:0} s | {Regex.Match(r.Detail ?? "", @"(\d+) piece").Groups[1].Value} | {r.Segments.Count} | {r.Segments.Count(s => s.Words is { Count: > 0 })} | {wer:P0} |\n");
            Assert.NotEmpty(r.Segments);
            Assert.All(r.Segments, s => Assert.True(s.EndMs >= s.StartMs));
            Assert.InRange(lastEnd, length * 1000 * 0.7, length * 1000 + 500); // times reach the end of the recording
        }
        var file = Environment.GetEnvironmentVariable("GITHUB_STEP_SUMMARY");
        if (!string.IsNullOrEmpty(file)) await File.AppendAllTextAsync(file, summary.ToString());
        output.WriteLine(summary.ToString());
    }

    private static string[] Words(string s) =>
        Regex.Replace(s.ToLowerInvariant(), @"[^\p{L}\p{N}' ]", " ").Split(' ', StringSplitOptions.RemoveEmptyEntries);

    private static double WordErrorRate(string correct, string heard)
    {
        var a = Words(correct); var b = Words(heard);
        var d = new int[a.Length + 1, b.Length + 1];
        for (var i = 0; i <= a.Length; i++) d[i, 0] = i;
        for (var j = 0; j <= b.Length; j++) d[0, j] = j;
        for (var i = 1; i <= a.Length; i++)
            for (var j = 1; j <= b.Length; j++)
                d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
        return a.Length == 0 ? 0 : (double)d[a.Length, b.Length] / a.Length;
    }
}
