using System.Diagnostics;
using VerbaFlow.Core.Providers;
using VerbaFlow.Core.Transcripts;
using VerbaFlow.Infrastructure.Azure;

namespace VerbaFlow.Infrastructure.Services;

/// <summary>
/// For recordings in two or three languages. Given several languages, Azure picks one for the whole audio, so a mixed
/// English and Afrikaans meeting comes out all in one language. This sends the recording in short pieces instead, each
/// with every chosen language, so Azure picks the language piece by piece.
/// 1. One pass over the whole recording in the first language, used only for who spoke when and for where the pauses are.
/// 2. The audio is cut at pauses into pieces of a few seconds, covering the whole recording, so nothing is skipped.
/// 3. Each piece is transcribed on its own with all the chosen languages; its times are moved back to the full recording.
/// 4. Speakers from step 1 are put back on the words (SpeakerAligner).
/// One language: the recording goes straight through, unchanged.
/// </summary>
public sealed class LanguagePerPieceSpeech(ISpeechService inner, string? ffmpegPath) : ISpeechService
{
    /// <summary>A piece is at least this long, so Azure has enough speech to tell the language.</summary>
    public const int MinPieceMs = 3000;
    /// <summary>A piece is cut here even without a pause.</summary>
    public const int MaxPieceMs = 15000;
    /// <summary>A gap between words at least this long counts as a pause where a piece may end.</summary>
    public const int PauseMs = 350;
    /// <summary>A piece Azure finds mixed is cut in two, but no half is made shorter than this.</summary>
    public const int MinSplitMs = 1000;
    /// <summary>How many times one piece may be cut again.</summary>
    public const int MaxSplits = 3;
    private const int SampleRate = 16000;

    public string Executable { get; } = string.IsNullOrWhiteSpace(ffmpegPath) ? "ffmpeg" : ffmpegPath!;

    public async Task<TranscriptionResult> TranscribeAsync(Stream audio, TranscribeOptions o, CancellationToken ct)
    {
        if (o.CandidateLanguages.Length < 2) return await inner.TranscribeAsync(audio, o, ct);

        var pcm = await ToPcmAsync(audio, ct);
        var totalMs = (int)(pcm.Length / 2 * 1000L / SampleRate);

        var first = await inner.TranscribeAsync(Wav(pcm, 0, pcm.Length), o with { CandidateLanguages = [o.CandidateLanguages[0]] }, ct);
        var pieces = Pieces(first.Segments, totalMs);
        var pauses = Pauses(first.Segments);

        var everyone = new Speaker(Guid.NewGuid(), "Speaker 1");
        var segments = new List<Segment>();
        var perLanguage = new Dictionary<string, int>();
        string? warning = first.Warning;
        var split = 0;
        var kept = 0;

        async Task PieceAsync(int start, int end, int depth)
        {
            var from = (long)start * SampleRate / 1000 * 2;
            var to = Math.Min(pcm.Length, (long)end * SampleRate / 1000 * 2);
            if (to - from < SampleRate / 5) return; // under 0.1 s
            TranscriptionResult r;
            try { r = await inner.TranscribeAsync(Wav(pcm, from, to - from), o with { Diarize = false }, ct); }
            catch (MixedLanguagesException)
            {
                // Both languages in one piece: cut it in two at the pause nearest its middle and try each half.
                if (depth < MaxSplits && end - start >= 2 * MinSplitMs)
                {
                    var middle = (start + end) / 2;
                    var cut = pauses.Where(c => c - start >= MinSplitMs && end - c >= MinSplitMs)
                        .OrderBy(c => Math.Abs(c - middle)).Cast<int?>().FirstOrDefault() ?? middle;
                    split++;
                    await PieceAsync(start, cut, depth + 1);
                    await PieceAsync(cut, end, depth + 1);
                    return;
                }
                // Too short to cut again: keep what the full pass heard there, in the first language.
                kept++;
                foreach (var s in first.Segments.Where(s => (s.StartMs + s.EndMs) / 2 >= start && (s.StartMs + s.EndMs) / 2 < end))
                    segments.Add(s with { SpeakerId = everyone.Id });
                return;
            }
            warning ??= r.Warning;
            foreach (var s in r.Segments)
            {
                segments.Add(s with
                {
                    SpeakerId = everyone.Id, StartMs = s.StartMs + start, EndMs = s.EndMs + start,
                    Words = s.Words?.Select(w => w with { StartMs = w.StartMs + start, EndMs = w.EndMs + start }).ToList(),
                });
            }
            foreach (var lang in r.Segments.Select(s => s.Language).Distinct()) perLanguage[lang] = perLanguage.GetValueOrDefault(lang) + 1;
        }

        foreach (var (start, end) in pieces) await PieceAsync(start, end, 0);
        segments.Sort((a, b) => a.StartMs.CompareTo(b.StartMs));

        var detail = $"{pieces.Count} pieces; " + string.Join(", ", perLanguage.OrderByDescending(x => x.Value).Select(x => $"{x.Key} in {x.Value}"))
            + (split > 0 ? $"; {split} cut again for mixed languages" : "")
            + (kept > 0 ? $"; {kept} too mixed, kept from the first-language pass" : "");
        var combined = new TranscriptionResult(first.Engine + "+language-per-piece", [everyone], AzureSpeechService.MergeTurns(segments), warning, detail);

        // Who spoke when comes from the full pass, which heard the whole conversation at once.
        var numbers = first.Speakers.Select((sp, i) => (sp.Id, i)).ToDictionary(x => x.Id, x => x.i);
        var turns = first.Segments.Select(s => new SpeakerTurn(s.StartMs, s.EndMs, numbers.GetValueOrDefault(s.SpeakerId))).ToList();
        return turns.Count == 0 ? combined : SpeakerAligner.Relabel(combined, turns);
    }

    /// <summary>
    /// Cuts the timeline into pieces that cover all of it. A piece ends at the first pause after it is at least
    /// <see cref="MinPieceMs"/> long, or at <see cref="MaxPieceMs"/> if there is no pause. Pauses come from the gaps
    /// between the words of the full pass, and between its phrases.
    /// </summary>
    public static List<(int Start, int End)> Pieces(IReadOnlyList<Segment> heard, int totalMs)
    {
        var cuts = Pauses(heard);
        var pieces = new List<(int, int)>();
        var start = 0;
        var next = 0;
        while (start < totalMs)
        {
            while (next < cuts.Count && cuts[next] - start < MinPieceMs) next++;
            var end = next < cuts.Count && cuts[next] - start <= MaxPieceMs ? cuts[next] : Math.Min(totalMs, start + MaxPieceMs);
            if (totalMs - end < MinPieceMs / 2) end = totalMs; // do not leave a scrap at the end
            pieces.Add((start, end));
            start = end;
        }
        return pieces;
    }

    /// <summary>The middle of every pause (a gap between words of at least <see cref="PauseMs"/>), in order.</summary>
    public static List<int> Pauses(IReadOnlyList<Segment> heard)
    {
        var words = heard.OrderBy(s => s.StartMs)
            .SelectMany(s => s.Words is { Count: > 0 } w ? w.Select(x => (x.StartMs, x.EndMs)) : [(s.StartMs, s.EndMs)])
            .OrderBy(x => x.StartMs).ToList();
        var cuts = new List<int>();
        for (var i = 1; i < words.Count; i++)
        {
            var gap = words[i].StartMs - words[i - 1].EndMs;
            if (gap >= PauseMs) cuts.Add(words[i - 1].EndMs + gap / 2);
        }
        if (words.Count > 0 && words[0].StartMs >= PauseMs) cuts.Insert(0, words[0].StartMs / 2);
        return cuts;
    }

    /// <summary>Decodes any audio to raw 16 kHz mono 16-bit samples with ffmpeg.</summary>
    private async Task<byte[]> ToPcmAsync(Stream audio, CancellationToken ct)
    {
        var dir = Directory.CreateTempSubdirectory("verbaflow-pieces-");
        try
        {
            var input = Path.Combine(dir.FullName, "in");
            var output = Path.Combine(dir.FullName, "out.raw");
            if (audio.CanSeek) audio.Position = 0;
            await using (var f = File.Create(input)) await audio.CopyToAsync(f, ct);
            var psi = new ProcessStartInfo(Executable) { RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
            foreach (var a in new[] { "-hide_banner", "-loglevel", "error", "-y", "-i", input, "-vn", "-ac", "1", "-ar", "16000", "-f", "s16le", output })
                psi.ArgumentList.Add(a);
            using var proc = Process.Start(psi) ?? throw new InvalidOperationException("ffmpeg did not start.");
            var err = proc.StandardError.ReadToEndAsync(ct);
            _ = proc.StandardOutput.ReadToEndAsync(ct);
            await proc.WaitForExitAsync(ct);
            if (proc.ExitCode != 0 || !File.Exists(output))
                throw new InvalidOperationException("ffmpeg could not read this audio to cut it into pieces. " + await err);
            return await File.ReadAllBytesAsync(output, ct);
        }
        finally { try { dir.Delete(true); } catch { /* temp folder */ } }
    }

    /// <summary>A WAV file (16 kHz mono 16-bit) holding part of the samples.</summary>
    public static MemoryStream Wav(byte[] pcm, long offset, long count)
    {
        var ms = new MemoryStream((int)count + 44);
        using (var w = new BinaryWriter(ms, System.Text.Encoding.ASCII, leaveOpen: true))
        {
            w.Write("RIFF"u8); w.Write((int)(36 + count)); w.Write("WAVE"u8);
            w.Write("fmt "u8); w.Write(16); w.Write((short)1); w.Write((short)1); w.Write(SampleRate); w.Write(SampleRate * 2); w.Write((short)2); w.Write((short)16);
            w.Write("data"u8); w.Write((int)count);
            w.Write(pcm, (int)offset, (int)count);
        }
        ms.Position = 0;
        return ms;
    }
}
