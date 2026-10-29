using VerbaFlow.Core.Domain;

namespace VerbaFlow.Core.Transcripts;

public sealed record Speaker(Guid Id, string Label);

/// <summary>An immutable passage: an edit never changes a segment, it creates a new one.</summary>
/// <summary>When one word was spoken, from the speech service. Used to follow along while the audio plays.</summary>
public sealed record WordTiming(string Text, int StartMs, int EndMs);

public sealed record Segment(Guid Id, Guid SpeakerId, string Language, int StartMs, int EndMs, string Text,
    double Confidence, bool LowConfidence, IReadOnlyList<WordTiming>? Words = null, bool? NewParagraph = null,
    string? Inserted = null, string? List = null, string? Baseline = null, bool? SplitPart = null);
// Baseline: the machine text this passage is compared with for tracked changes, set when a box was split (each part gets
// its share of the original). SplitPart: true on the boxes a split added after the first, which have no passage of their
// own in machine version 1; the others line up with version 1 passage by passage.
// NewParagraph: in a dictation, true where a paragraph starts and false where the passage carries on the paragraph before it.
// Null (meetings, and dictations saved before this existed) means every passage stands alone.
// List: in a dictation, "bullet" or "number" on a passage that starts a list point ("bullet point", "numbered point").
// Inserted: in a dictation, the codes of saved text dropped into this passage by "insert <code>" (e.g. "legal 1.1").

/// <summary>AutoTidy: the app crossed out restarted sentences in a dictation; shown as tracked changes against version 1.</summary>
public enum VersionKind { MachineV1, HumanEdit, SpeakerCorrection, Restore, ReopenCopy, AutoTidy }

public sealed class TranscriptVersion
{
    public int No { get; init; }
    public VersionKind Kind { get; init; }
    public int? ParentNo { get; init; }
    public Guid? CreatedBy { get; init; }
    public string Capacity { get; init; } = "";
    public DateTimeOffset CreatedAt { get; init; }
    public List<Guid> SegmentIds { get; init; } = [];
    public Dictionary<Guid, string> SpeakerNames { get; init; } = [];
    public string? Note { get; init; }
}

public sealed record RenderedSegment(Segment Segment, string SpeakerName);

/// <summary>
/// Version 1 is always the machine output and is never changed. Every correction (text or speaker label)
/// appends a new version that points at unchanged segments plus any new ones.
/// </summary>
public sealed class Transcript
{
    public Guid ItemId { get; init; }
    public List<Speaker> Speakers { get; init; } = [];
    public Dictionary<Guid, Segment> Segments { get; init; } = [];
    public List<TranscriptVersion> Versions { get; init; } = [];
    public string Engine { get; init; } = "";

    public TranscriptVersion Current => Versions[^1];

    public static Transcript CreateMachineV1(Guid itemId, string engine, IEnumerable<Speaker> speakers,
        IEnumerable<Segment> segments, DateTimeOffset now)
    {
        var t = new Transcript { ItemId = itemId, Engine = engine };
        t.Speakers.AddRange(speakers);
        var ordered = segments.OrderBy(s => s.StartMs).ToList();
        foreach (var s in ordered) t.Segments[s.Id] = s;
        t.Versions.Add(new TranscriptVersion
        {
            No = 1, Kind = VersionKind.MachineV1, Capacity = "machine", CreatedAt = now,
            SegmentIds = ordered.Select(s => s.Id).ToList(),
            SpeakerNames = t.Speakers.ToDictionary(s => s.Id, s => s.Label)
        });
        return t;
    }

    public TranscriptVersion Version(int no) =>
        Versions.FirstOrDefault(v => v.No == no) ?? throw new NotFoundException($"Version {no} does not exist.");

    public IReadOnlyList<RenderedSegment> Render(int? versionNo = null)
    {
        var v = versionNo is null ? Current : Version(versionNo.Value);
        return v.SegmentIds.Select(id => new RenderedSegment(Segments[id],
            v.SpeakerNames.GetValueOrDefault(Segments[id].SpeakerId, "Unknown"))).ToList();
    }

    public TranscriptVersion EditSegment(Guid segmentId, string newText, Guid actor, string capacity, DateTimeOffset now, string? note = null)
    {
        // Emptying a passage is allowed (its words were moved elsewhere, or should not be there): it stays in the version
        // with no text, so the tracked changes still show what was taken out and Restore brings it back.
        var cur = Current;
        var index = cur.SegmentIds.IndexOf(segmentId);
        if (index < 0) throw new DomainException("That passage is not part of the current version.");
        var old = Segments[segmentId];
        var trimmed = Tidy(newText, out var startsParagraph);
        // A line break typed at the very start makes the passage begin a new paragraph (used in dictations).
        var newParagraph = startsParagraph && index > 0 ? true : old.NewParagraph;
        if (old.Text == trimmed && newParagraph == old.NewParagraph) throw new DomainException("The text has not changed.");
        var replacement = old with
        {
            Id = Guid.NewGuid(), Text = trimmed, NewParagraph = newParagraph,
            // Only a new paragraph, no change to the words: the passage keeps its confidence.
            LowConfidence = old.Text == trimmed && old.LowConfidence, Words = RetimedWords(old.Words, trimmed)
        };
        Segments[replacement.Id] = replacement;
        var ids = cur.SegmentIds.ToList();
        ids[index] = replacement.Id;
        return Append(VersionKind.HumanEdit, actor, capacity, now, ids, cur.SpeakerNames, note ?? $"Edited passage at {old.StartMs} ms");
    }

    /// <summary>In a dictation: the passage carries on the paragraph before it instead of starting a new one. The words do not change.</summary>
    public TranscriptVersion JoinWithPrevious(Guid segmentId, Guid actor, string capacity, DateTimeOffset now)
    {
        var cur = Current;
        var index = cur.SegmentIds.IndexOf(segmentId);
        if (index < 0) throw new DomainException("That passage is not part of the current version.");
        if (index == 0) throw new DomainException("The first paragraph has nothing before it to join.");
        var old = Segments[segmentId];
        if (old.NewParagraph == false) throw new DomainException("This already carries on the paragraph before it.");
        var replacement = old with { Id = Guid.NewGuid(), NewParagraph = false, List = null };
        Segments[replacement.Id] = replacement;
        var ids = cur.SegmentIds.ToList();
        ids[index] = replacement.Id;
        return Append(VersionKind.HumanEdit, actor, capacity, now, ids, cur.SpeakerNames, $"Joined passage at {old.StartMs} ms to the paragraph before");
    }

    /// <summary>
    /// In a meeting: the passage's words move up into the nearest passage above that has text, which keeps its speaker.
    /// The passage itself stays in the version with no text, so the count of passages does not change (tracked changes
    /// compare passage by passage) and Restore brings it back. Word timings move with the words.
    /// </summary>
    public TranscriptVersion MergeWithPrevious(Guid segmentId, Guid actor, string capacity, DateTimeOffset now)
    {
        var cur = Current;
        var index = cur.SegmentIds.IndexOf(segmentId);
        if (index < 0) throw new DomainException("That passage is not part of the current version.");
        var lower = Segments[segmentId];
        if (lower.Text.Length == 0) throw new DomainException("This box is empty, so there is nothing to move up.");
        var above = index - 1;
        while (above >= 0 && Segments[cur.SegmentIds[above]].Text.Length == 0) above--;
        if (above < 0) throw new DomainException("There is no box above this one to join it to.");
        var upper = Segments[cur.SegmentIds[above]];
        var words = upper.Words is { } a && lower.Words is { } b ? a.Concat(b).ToList() : null;
        var merged = upper with
        {
            Id = Guid.NewGuid(), Text = upper.Text + " " + lower.Text, EndMs = Math.Max(upper.EndMs, lower.EndMs), Words = words,
            LowConfidence = upper.LowConfidence || lower.LowConfidence, Confidence = Math.Min(upper.Confidence, lower.Confidence)
        };
        var emptied = lower with { Id = Guid.NewGuid(), Text = "", Words = [] };
        Segments[merged.Id] = merged;
        Segments[emptied.Id] = emptied;
        var ids = cur.SegmentIds.ToList();
        ids[above] = merged.Id;
        ids[index] = emptied.Id;
        return Append(VersionKind.HumanEdit, actor, capacity, now, ids, cur.SpeakerNames, $"Joined the box at {lower.StartMs} ms to the box above");
    }

    /// <summary>
    /// The machine text each passage of a version is compared with for tracked changes (null where it cannot be told).
    /// A split part carries its own; the others line up with machine version 1 passage by passage once split parts are left out.
    /// </summary>
    public IReadOnlyList<string?> Baselines(int? versionNo = null)
    {
        var v = versionNo is null ? Current : Version(versionNo.Value);
        var machine = Versions[0].SegmentIds;
        var segs = v.SegmentIds.Select(id => Segments[id]).ToList();
        var lined = segs.Count(s => s.SplitPart != true) == machine.Count;
        var result = new List<string?>(segs.Count);
        var k = 0;
        foreach (var s in segs)
        {
            string? m = null;
            if (s.SplitPart != true) { if (lined) m = Segments[machine[k]].Text; k++; }
            result.Add(s.Baseline ?? m);
        }
        return result;
    }

    /// <summary>
    /// In a meeting: words <paramref name="fromWord"/> up to (not including) <paramref name="toWord"/> of a box were said by
    /// someone else. Those words become a box of their own with that speaker; words before and after stay with the box's
    /// speaker in boxes of their own (so one box can become up to three). Word timings move with the words. Each part
    /// keeps its share of the original text so tracked changes still line up. The other person is an existing speaker, or
    /// a new one named here. Returns the version, the speaker given the words and how many boxes there are now.
    /// </summary>
    public (TranscriptVersion Version, Guid SpeakerId, string SpeakerName, int Boxes) GiveToSpeaker(Guid segmentId, int fromWord, int toWord,
        Guid? speakerId, string? newSpeakerName, Guid actor, string capacity, DateTimeOffset now)
    {
        var cur = Current;
        var index = cur.SegmentIds.IndexOf(segmentId);
        if (index < 0) throw new DomainException("That passage is not part of the current version.");
        var old = Segments[segmentId];
        var spans = System.Text.RegularExpressions.Regex.Matches(old.Text, @"\S+").ToList();
        if (spans.Count == 0) throw new DomainException("This box is empty.");
        if (fromWord < 0 || toWord > spans.Count || fromWord >= toWord) throw new DomainException("Choose the words the other person said.");

        var names = new Dictionary<Guid, string>(cur.SpeakerNames);
        Guid who;
        var typed = newSpeakerName?.Trim() ?? "";
        if (typed.Length > 0)
        {
            if (typed.Length > 100) throw new DomainException("That name is too long.");
            var same = Speakers.FirstOrDefault(s => string.Equals(names.GetValueOrDefault(s.Id, s.Label), typed, StringComparison.OrdinalIgnoreCase));
            if (same is not null) who = same.Id;
            else
            {
                who = Guid.NewGuid();
                Speakers.Add(new Speaker(who, $"Speaker {Speakers.Count + 1}"));
                names[who] = typed;
            }
        }
        else if (speakerId is { } sid && Speakers.Any(s => s.Id == sid)) who = sid;
        else throw new DomainException("Choose who said these words.");
        if (who == old.SpeakerId) throw new DomainException("These words are already given to that speaker.");
        names.TryAdd(who, Speakers.First(s => s.Id == who).Label);

        // Each word's timing: the speech service's when they line up with the words, else spread across the box as the page does.
        var timed = old.Words is { } ws && ws.Count == spans.Count ? ws.ToList() : null;
        var total = spans.Sum(m => m.Length + 1);
        var span = Math.Max(0, old.EndMs - old.StartMs);
        int Est(int w) => old.StartMs + (int)Math.Round((double)spans.Take(w).Sum(m => m.Length + 1) / total * span);
        int StartOf(int w) => timed?[w].StartMs ?? Est(w);
        int EndOf(int w) => timed?[w].EndMs ?? Est(w + 1);

        var cuts = new List<(int From, int To, Guid Speaker)>();
        if (fromWord > 0) cuts.Add((0, fromWord, old.SpeakerId));
        cuts.Add((fromWord, toWord, who));
        if (toWord < spans.Count) cuts.Add((toWord, spans.Count, old.SpeakerId));

        // Share out the original text: each original word goes with the part holding the word it matches, and a word taken
        // out goes with the next matched word's part.
        var baseline = Baselines()[index] ?? old.Text;
        var shown = spans.Select(m => m.Value).ToArray();
        var orig = WordDiff.Words(baseline.Replace('\n', ' '));
        var partOfShown = new int[shown.Length];
        for (var c = 0; c < cuts.Count; c++) for (var w = cuts[c].From; w < cuts[c].To; w++) partOfShown[w] = c;
        var match = MatchWords(orig, shown);
        var shares = cuts.Select(_ => new List<string>()).ToList();
        for (var j = 0; j < orig.Length; j++)
        {
            var next = -1;
            for (var x = j; x < orig.Length && next < 0; x++) if (match[x] >= 0) next = match[x];
            shares[next >= 0 ? partOfShown[next] : cuts.Count - 1].Add(orig[j]);
        }

        var ids = cur.SegmentIds.ToList();
        ids.RemoveAt(index);
        for (var c = 0; c < cuts.Count; c++)
        {
            var (from, to, speaker) = cuts[c];
            var text = old.Text[spans[from].Index..(spans[to - 1].Index + spans[to - 1].Length)].Trim();
            var part = old with
            {
                Id = Guid.NewGuid(), SpeakerId = speaker, Text = text,
                StartMs = c == 0 ? old.StartMs : StartOf(from), EndMs = c == cuts.Count - 1 ? old.EndMs : EndOf(to - 1),
                Words = timed?.Skip(from).Take(to - from).ToList(), Baseline = string.Join(' ', shares[c]),
                SplitPart = c == 0 ? old.SplitPart : true
            };
            Segments[part.Id] = part;
            ids.Insert(index + c, part.Id);
        }
        var whoName = names[who];
        var words = string.Join(' ', shown[fromWord..toWord]);
        var v = Append(VersionKind.HumanEdit, actor, capacity, now, ids, names,
            $"Gave {(toWord - fromWord == 1 ? $"\"{words}\"" : $"{toWord - fromWord} words")} at {StartOf(fromWord)} ms to {whoName}");
        return (v, who, whoName, cuts.Count);
    }

    /// <summary>
    /// For each word of <paramref name="a"/>, the index of the word of <paramref name="b"/> it matches (longest common
    /// subsequence, ignoring capitals and punctuation), or -1.
    /// </summary>
    private static int[] MatchWords(string[] wa, string[] wb)
    {
        static string Bare(string w) { var x = new string(w.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant(); return x.Length > 0 ? x : w; }
        var a = wa.Select(Bare).ToArray();
        var b = wb.Select(Bare).ToArray();
        var l = new int[a.Length + 1, b.Length + 1];
        for (var i = a.Length - 1; i >= 0; i--)
            for (var j = b.Length - 1; j >= 0; j--)
                l[i, j] = a[i] == b[j] ? l[i + 1, j + 1] + 1 : Math.Max(l[i + 1, j], l[i, j + 1]);
        var m = Enumerable.Repeat(-1, a.Length).ToArray();
        int x = 0, y = 0;
        while (x < a.Length && y < b.Length)
        {
            if (a[x] == b[y]) { m[x] = y; x++; y++; }
            else if (l[x + 1, y] >= l[x, y + 1]) x++;
            else y++;
        }
        return m;
    }

    /// <summary>
    /// Changes every whole-word match of <paramref name="from"/> (any capitals, any spacing between its words) to
    /// <paramref name="to"/> in every passage of the current version, as one version. A match that starts with a capital
    /// keeps it. Passages holding saved text are left alone. Returns null when nothing matched.
    /// </summary>
    public (TranscriptVersion Version, int Passages, int Changes)? ReplaceEverywhere(string from, string to, Guid actor, string capacity, DateTimeOffset now)
    {
        var parts = from.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        to = to.Trim();
        if (parts.Length == 0 || parts.Length > 8 || to.Length > 200) throw new DomainException("Give the words to change, up to 8 of them.");
        if (string.Equals(string.Join(' ', parts), to, StringComparison.Ordinal)) throw new DomainException("The new words are the same as the old ones.");
        var pattern = new System.Text.RegularExpressions.Regex(
            @"(?<![\p{L}\p{N}])" + string.Join(@"\s+", parts.Select(System.Text.RegularExpressions.Regex.Escape)) + @"(?![\p{L}\p{N}])",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant,
            TimeSpan.FromSeconds(1));
        var cur = Current;
        var ids = cur.SegmentIds.ToList();
        int passages = 0, changes = 0;
        for (var i = 0; i < ids.Count; i++)
        {
            var old = Segments[ids[i]];
            if (old.Text.Length == 0 || old.Inserted is not null) continue;
            var n = 0;
            var text = pattern.Replace(old.Text, m =>
            {
                n++;
                return char.IsUpper(m.Value[0]) && to.Length > 0 && char.IsLower(to[0]) ? char.ToUpper(to[0]) + to[1..] : to;
            });
            if (n == 0 || text == old.Text) continue;
            var replacement = old with { Id = Guid.NewGuid(), Text = text, Words = RetimedWords(old.Words, text) };
            Segments[replacement.Id] = replacement;
            ids[i] = replacement.Id;
            passages++;
            changes += n;
        }
        if (passages == 0) return null;
        var v = Append(VersionKind.HumanEdit, actor, capacity, now, ids, cur.SpeakerNames, $"Changed \"{string.Join(' ', parts)}\" to \"{to}\" everywhere");
        return (v, passages, changes);
    }

    /// <summary>
    /// The app's own corrections to the machine text (restarted sentences in a dictation), as a version of their own so
    /// the original stays visible as tracked changes and Restore brings it back. Not counted as a person's correction.
    /// </summary>
    public TranscriptVersion? AddMachineTidy(IReadOnlyDictionary<Guid, Segment> replacements, DateTimeOffset now)
    {
        if (replacements.Count == 0) return null;
        var ids = Current.SegmentIds.Select(id =>
        {
            if (!replacements.TryGetValue(id, out var r)) return id;
            Segments[r.Id] = r;
            return r.Id;
        }).ToList();
        var v = new TranscriptVersion
        {
            No = Current.No + 1, Kind = VersionKind.AutoTidy, ParentNo = Current.No, CreatedBy = null, Capacity = "machine",
            CreatedAt = now, SegmentIds = ids, SpeakerNames = new(Current.SpeakerNames), Note = "Restarted sentences crossed out"
        };
        Versions.Add(v);
        return v;
    }

    /// <summary>How many versions hold a person's work (not the machine's own versions).</summary>
    [System.Text.Json.Serialization.JsonIgnore] public int PersonVersions => Versions.Count(v => v.Kind is not (VersionKind.MachineV1 or VersionKind.AutoTidy));

    /// <summary>
    /// Trims the text. A line break inside it is kept as a paragraph break (one per break, no blank lines, no spaces
    /// around it); <paramref name="startsParagraph"/> says whether the text began with one.
    /// </summary>
    public static string Tidy(string text, out bool startsParagraph)
    {
        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        startsParagraph = lines.Length > 1 && lines[0].Trim().Length == 0 && lines.Skip(1).Any(l => l.Trim().Length > 0);
        return string.Join("\n", lines.Select(l => l.Trim()).Where(l => l.Length > 0));
    }

    /// <summary>
    /// A correction that keeps the same number of words (a spelling fix, say) keeps each word's timing.
    /// Anything else drops word timings; the player then spreads the words evenly across the passage.
    /// </summary>
    private static IReadOnlyList<WordTiming>? RetimedWords(IReadOnlyList<WordTiming>? words, string newText)
    {
        if (words is null) return null;
        var tokens = newText.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length != words.Count) return null;
        return words.Select((w, i) => w with { Text = tokens[i] }).ToList();
    }

    public TranscriptVersion RenameSpeaker(Guid speakerId, string displayName, Guid actor, string capacity, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(displayName)) throw new DomainException("A speaker name is required.");
        if (Speakers.All(s => s.Id != speakerId)) throw new NotFoundException("Unknown speaker.");
        var names = new Dictionary<Guid, string>(Current.SpeakerNames) { [speakerId] = displayName.Trim() };
        return Append(VersionKind.SpeakerCorrection, actor, capacity, now, Current.SegmentIds, names,
            $"Renamed speaker to {displayName.Trim()}");
    }

    public TranscriptVersion Restore(int versionNo, Guid actor, string capacity, DateTimeOffset now)
    {
        var v = Version(versionNo);
        return Append(VersionKind.Restore, actor, capacity, now, v.SegmentIds, v.SpeakerNames, $"Restored version {versionNo}");
    }

    /// <summary>A copy for a reopened item. History, including machine version 1, is preserved.</summary>
    public Transcript ForkFor(Guid newItemId, Guid actor, DateTimeOffset now)
    {
        var copy = new Transcript { ItemId = newItemId, Engine = Engine };
        copy.Speakers.AddRange(Speakers);
        foreach (var kv in Segments) copy.Segments[kv.Key] = kv.Value;
        foreach (var v in Versions)
            copy.Versions.Add(new TranscriptVersion
            {
                No = v.No, Kind = v.Kind, ParentNo = v.ParentNo, CreatedBy = v.CreatedBy, Capacity = v.Capacity,
                CreatedAt = v.CreatedAt, SegmentIds = [.. v.SegmentIds], SpeakerNames = new(v.SpeakerNames), Note = v.Note
            });
        copy.Append(VersionKind.ReopenCopy, actor, "system", now, Current.SegmentIds, Current.SpeakerNames,
            "Working copy created when the item was reopened");
        return copy;
    }

    private TranscriptVersion Append(VersionKind kind, Guid actor, string capacity, DateTimeOffset now,
        IEnumerable<Guid> segmentIds, Dictionary<Guid, string> names, string note)
    {
        var v = new TranscriptVersion
        {
            No = Current.No + 1, Kind = kind, ParentNo = Current.No, CreatedBy = actor, Capacity = capacity,
            CreatedAt = now, SegmentIds = segmentIds.ToList(), SpeakerNames = new(names), Note = note
        };
        Versions.Add(v);
        return v;
    }
}
