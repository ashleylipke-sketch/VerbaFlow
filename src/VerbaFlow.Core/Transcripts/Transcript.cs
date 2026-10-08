using VerbaFlow.Core.Domain;

namespace VerbaFlow.Core.Transcripts;

public sealed record Speaker(Guid Id, string Label);

/// <summary>An immutable passage: an edit never changes a segment, it creates a new one.</summary>
/// <summary>When one word was spoken, from the speech service. Used to follow along while the audio plays.</summary>
public sealed record WordTiming(string Text, int StartMs, int EndMs);

public sealed record Segment(Guid Id, Guid SpeakerId, string Language, int StartMs, int EndMs, string Text,
    double Confidence, bool LowConfidence, IReadOnlyList<WordTiming>? Words = null);

public enum VersionKind { MachineV1, HumanEdit, SpeakerCorrection, Restore, ReopenCopy }

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

    public TranscriptVersion EditSegment(Guid segmentId, string newText, Guid actor, string capacity, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(newText)) throw new DomainException("The text cannot be empty.");
        var cur = Current;
        var index = cur.SegmentIds.IndexOf(segmentId);
        if (index < 0) throw new DomainException("That passage is not part of the current version.");
        var old = Segments[segmentId];
        if (old.Text == newText.Trim()) throw new DomainException("The text has not changed.");
        var trimmed = newText.Trim();
        var replacement = old with { Id = Guid.NewGuid(), Text = trimmed, LowConfidence = false, Words = RetimedWords(old.Words, trimmed) };
        Segments[replacement.Id] = replacement;
        var ids = cur.SegmentIds.ToList();
        ids[index] = replacement.Id;
        return Append(VersionKind.HumanEdit, actor, capacity, now, ids, cur.SpeakerNames, $"Edited passage at {old.StartMs} ms");
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
