using System.Diagnostics;
using VerbaFlow.Core.Providers;
using VerbaFlow.Core.Transcripts;
using VerbaFlow.Infrastructure.Azure;

namespace VerbaFlow.Infrastructure.Services;

/// <summary>
/// For recordings in two or three languages. Given several languages, Azure picks one for the whole audio, so a mixed
/// English and Afrikaans meeting comes out all in one language. This sends the recording in short pieces instead, each
/// with every chosen language, so Azure picks the language piece by piece.
/// 1. One pass over the whole recording in each chosen language, used only for who spoke when and where to cut.
/// 2. The audio is cut where a speaker changes or a sentence ends (else at a pause) into pieces of a few seconds,
/// covering the whole recording, so nothing is skipped.
/// 3. Each piece is transcribed on its own with all the chosen languages; its times are moved back to the full recording.
/// 4. Speakers are put back on the words (SpeakerAligner) from the step-1 pass in the language heard most, because
/// speakers are told apart better in the main language.
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

        // One full pass per chosen language. Each hears where sentences end and voices change best in its own
        // language, so together they show where to cut; the pass in the main language later gives the speakers.
        var passes = new Dictionary<string, TranscriptionResult>();
        foreach (var lang in o.CandidateLanguages.Distinct())
            passes[lang] = await inner.TranscribeAsync(Wav(pcm, 0, pcm.Length), o with { CandidateLanguages = [lang] }, ct);
        var first = passes[o.CandidateLanguages[0]];
        var pieces = Pieces(passes.Values.Select(p => p.Segments).ToList(), totalMs);
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

        // Speakers are told apart best in the language most of the meeting is in.
        var main = MainLanguage(segments, o.CandidateLanguages) ?? o.CandidateLanguages[0];
        var speakerPass = passes[main];
        warning ??= speakerPass.Warning;

        // Speech the full pass heard but no piece returned (Azure sometimes drops words near the edge of a piece):
        // send that stretch again on its own, and if it still comes back empty keep the full pass's words for it.
        var refilled = 0;
        foreach (var (gapStart, gapEnd, heardWords) in Missed(speakerPass.Segments, segments))
        {
            var from = (long)gapStart * SampleRate / 1000 * 2;
            var to = Math.Min(pcm.Length, (long)gapEnd * SampleRate / 1000 * 2);
            IReadOnlyList<Segment> again = [];
            try { again = (await inner.TranscribeAsync(Wav(pcm, from, to - from), o with { Diarize = false }, ct)).Segments; }
            catch (MixedLanguagesException) { }
            refilled++;
            if (again.Any(x => !string.IsNullOrWhiteSpace(x.Text)))
                segments.AddRange(again.Select(x => x with
                {
                    SpeakerId = everyone.Id, StartMs = x.StartMs + gapStart, EndMs = x.EndMs + gapStart,
                    Words = x.Words?.Select(w => w with { StartMs = w.StartMs + gapStart, EndMs = w.EndMs + gapStart }).ToList(),
                }));
            else
                segments.Add(heardWords);
        }
        segments.Sort((a, b) => a.StartMs.CompareTo(b.StartMs));

        var detail = $"{pieces.Count} pieces; " + string.Join(", ", perLanguage.OrderByDescending(x => x.Value).Select(x => $"{x.Key} in {x.Value}"))
            + (split > 0 ? $"; {split} cut again for mixed languages" : "")
            + (kept > 0 ? $"; {kept} too mixed, kept from the first-language pass" : "")
            + (refilled > 0 ? $"; {refilled} missed stretches sent again" : "")
            + $"; speakers worked out in {main}";
        var combined = new TranscriptionResult(first.Engine + "+language-per-piece", [everyone], AzureSpeechService.MergeTurns(segments), warning, detail);

        // Who spoke when comes from the full pass, which heard the whole conversation at once.
        var numbers = speakerPass.Speakers.Select((sp, i) => (sp.Id, i)).ToDictionary(x => x.Id, x => x.i);
        var turns = speakerPass.Segments.Select(s => new SpeakerTurn(s.StartMs, s.EndMs, numbers.GetValueOrDefault(s.SpeakerId))).ToList();
        return turns.Count == 0 ? combined : SpeakerAligner.Relabel(combined, turns);
    }

    /// <summary>
    /// Stretches where the full pass heard at least <see cref="MissedMinMs"/> of words that no piece returned. Each comes
    /// with a little margin and with the full pass's own words for it, as one phrase.
    /// </summary>
    public static List<(int Start, int End, Segment Heard)> Missed(IReadOnlyList<Segment> full, IReadOnlyList<Segment> pieces)
    {
        const int margin = 250;
        var covered = pieces.SelectMany(s => s.Words is { Count: > 0 } w ? w.Select(x => (x.StartMs, x.EndMs)) : [(s.StartMs, s.EndMs)])
            .Select(x => (Start: x.StartMs - margin, End: x.EndMs + margin)).ToList();
        bool Covered(int start, int end) => covered.Any(c => c.Start < end && start < c.End);

        var result = new List<(int, int, Segment)>();
        foreach (var seg in full.OrderBy(x => x.StartMs))
        {
            var words = seg.Words is { Count: > 0 } w ? w : [new WordTiming(seg.Text, seg.StartMs, seg.EndMs)];
            var run = new List<WordTiming>();
            void Flush()
            {
                if (run.Count > 0 && run[^1].EndMs - run[0].StartMs >= MissedMinMs)
                    result.Add((Math.Max(0, run[0].StartMs - margin), run[^1].EndMs + margin,
                        seg with { Id = Guid.NewGuid(), StartMs = run[0].StartMs, EndMs = run[^1].EndMs, Text = string.Join(" ", run.Select(x => x.Text)), Words = run.ToList() }));
                run.Clear();
            }
            foreach (var word in words)
            {
                if (Covered(word.StartMs, word.EndMs)) Flush();
                else run.Add(word);
            }
            Flush();
        }
        return result;
    }

    /// <summary>The chosen language heard for the most time across the pieces, or null if none was recognised.</summary>
    public static string? MainLanguage(IEnumerable<Segment> heard, string[] chosen)
    {
        static string Base(string l) => l.Split('-')[0].ToLowerInvariant();
        return heard.GroupBy(s => Base(s.Language))
            .Select(g => (Lang: chosen.FirstOrDefault(c => Base(c) == g.Key), Ms: g.Sum(s => s.EndMs - s.StartMs)))
            .Where(x => x.Lang is not null).OrderByDescending(x => x.Ms).Select(x => x.Lang).FirstOrDefault();
    }

    /// <summary>A change of speaker may end a piece this short, so a quick question and its answer are kept apart.</summary>
    public const int MinTurnPieceMs = 1500;

    /// <summary>Speech the full pass heard but the pieces left out is sent again when it is at least this long.</summary>
    public const int MissedMinMs = 600;

    public static List<(int Start, int End)> Pieces(IReadOnlyList<Segment> heard, int totalMs) => Pieces([heard], totalMs);

    /// <summary>
    /// Cuts the timeline into pieces that cover all of it, at most <see cref="MaxPieceMs"/> long. In order of preference
    /// a piece ends where a full pass heard the speaker change (at least <see cref="MinTurnPieceMs"/> in, because a new
    /// speaker often means a new language), where a full pass ended a phrase (the end of a sentence; at least
    /// <see cref="MinPieceMs"/> in), at a pause between words, or at the limit.
    /// </summary>
    public static List<(int Start, int End)> Pieces(IReadOnlyList<IReadOnlyList<Segment>> passes, int totalMs)
    {
        var turnChanges = passes.SelectMany(TurnChanges).Order().ToList();
        var phraseEnds = passes.SelectMany(PhraseEnds).Order().ToList();
        var pauses = passes.SelectMany(Pauses).Order().ToList();
        var pieces = new List<(int, int)>();
        var start = 0;
        while (start < totalMs)
        {
            bool InRange(int c, int min) => c - start >= min && c - start <= MaxPieceMs;
            int? end = turnChanges.Where(c => InRange(c, MinTurnPieceMs)).Cast<int?>().FirstOrDefault()
                ?? phraseEnds.Where(c => InRange(c, MinPieceMs)).Cast<int?>().FirstOrDefault()
                ?? pauses.Where(c => InRange(c, MinPieceMs)).Cast<int?>().FirstOrDefault();
            var e = end ?? Math.Min(totalMs, start + MaxPieceMs);
            if (totalMs - e < MinTurnPieceMs / 2) e = totalMs; // do not leave a scrap at the end
            pieces.Add((start, e));
            start = e;
        }
        return pieces;
    }

    /// <summary>The middle of the gap between two phrases of one pass that were given to different speakers.</summary>
    public static List<int> TurnChanges(IReadOnlyList<Segment> heard)
    {
        var phrases = heard.OrderBy(s => s.StartMs).ToList();
        var ends = PhraseEnds(heard);
        return Enumerable.Range(1, phrases.Count - 1 < 0 ? 0 : phrases.Count - 1)
            .Where(i => phrases[i].SpeakerId != phrases[i - 1].SpeakerId).Select(i => ends[i - 1]).ToList();
    }

    /// <summary>The middle of the gap between one phrase of the full pass and the next, in order.</summary>
    public static List<int> PhraseEnds(IReadOnlyList<Segment> heard)
    {
        var phrases = heard.OrderBy(s => s.StartMs).ToList();
        var ends = new List<int>();
        for (var i = 1; i < phrases.Count; i++)
        {
            var end = Math.Max(phrases[i - 1].EndMs, phrases[i - 1].Words?.LastOrDefault()?.EndMs ?? 0);
            var next = phrases[i].Words?.FirstOrDefault()?.StartMs ?? phrases[i].StartMs;
            ends.Add(next > end ? end + (next - end) / 2 : next);
        }
        return ends;
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
