using VerbaFlow.Core.Domain;

namespace VerbaFlow.Infrastructure.Services;

public sealed record PlatformPolicy(long MaxUploadBytes = 500L * 1024 * 1024, bool BlockSelfApproval = false);

public sealed record MarkerInput(MarkerType Type, int OffsetMs, string? Note);

public sealed record RecordingUpload(Stream Audio, string FileName, string ContentType, string Name, int LengthMs,
    string OutputLanguage, bool ConsentNoticeGiven, IReadOnlyList<MarkerInput> Markers, string? SpokenLanguages = null, int? NumSpeakers = null,
    ItemMode Mode = ItemMode.Meeting, string? ClientReference = null, string? DocumentType = null, LiveUpload? Live = null);

/// <summary>
/// Speech mode: the text heard live (null when the live session had already ended, for example after a restart) and how
/// far behind the speaker the text appeared on the person's screen, measured by their browser.
/// </summary>
public sealed record LiveUpload(LiveTranscript? Text, int? DelayMedianMs = null, int? DelayWorstMs = null);

/// <summary>A save of an unfinished dictation. Name, type, reference and languages are used only on the first save.</summary>
/// <param name="Live">Speech mode: the text heard live so far, kept with each save.</param>
public sealed record DraftUpload(Stream Audio, string FileName, string ContentType, int LengthMs, string? Name = null, string OutputLanguage = "en",
    string? SpokenLanguages = null, string? ClientReference = null, string? DocumentType = null, ItemMode Mode = ItemMode.Dictate, LiveUpload? Live = null);

public sealed record ImportUpload(Stream File, string FileName, string ContentType, string? Name, int LengthMs,
    string OutputLanguage, bool RightsConfirmed, string? SourceNote, string? SpokenLanguages = null, int? NumSpeakers = null,
    ItemMode Mode = ItemMode.Meeting, string? ClientReference = null, string? DocumentType = null);

/// <summary>A recording brought back by Meet's note-taker. <paramref name="Requesters"/>: first asker first (the owner).</summary>
public sealed record OnlineMeetingUpload(Stream Video, string FileName, string ContentType, string Name, IReadOnlyList<Guid> Requesters,
    string Platform, string NoteTakerName, Guid NoteTakerId, string SpokenLanguages, IReadOnlyList<PlatformSpeech> Speakers);

public sealed record RetryRequest(string? SpokenLanguages);
public sealed record RerunRequest(int? NumSpeakers, string? SpokenLanguages = null, bool DiscardEdits = false, string? Method = null, string? Audio = null, string? DictationService = null);

public sealed record DetailsUpdate(string? Name, string? ClientReference, string? Description, Priority? Priority,
    DateOnly? DueDate, bool ClearDueDate);

public sealed record ListQuery(IReadOnlySet<ItemStatus>? Statuses, bool? SelfAssigned, string? Search, ItemMode? Mode = null);

public sealed record ItemRow(
    Guid Id, string Name, string Priority, string Status, ItemStatus StatusCode, int LengthMs, string? ClientReference,
    string? Description, string? AssignedTo, string? Author, DateTimeOffset CreatedOn, DateOnly? DueDate, bool Overdue,
    IReadOnlyList<string> Tags, IReadOnlyList<string> Actions, int VersionNo, string? FailureReason = null, bool Outstanding = false);

public sealed record MarkerView(string Type, int OffsetMs, string? Note, string By, DateTimeOffset At);

public sealed record ItemDetail(
    ItemRow Row, string Owner, string OwnerRole, string? ProcessingState, string? FailureReason, string? OriginalFileName,
    string? ExternalSourceNote, string? OriginalSha256, IReadOnlyList<MarkerView> Markers, bool CanEditTranscript,
    bool CanEditDetails, bool CanDownloadAudio, string Role, string OutputLanguage, DateTimeOffset? ApprovedAt,
    string? ApprovedBy, Guid ChainId, IReadOnlyList<ChainEntry> Chain, string Provenance, string SpokenLanguages = "en",
    DateTimeOffset? DeletedAt = null, string? DeletedBy = null, string? DeleteReason = null, bool CanExport = false,
    string Mode = "Meeting", string? DocumentType = null, string? ClientReference = null, bool Video = false, IReadOnlyList<string>? Attendees = null);

public sealed record ChainEntry(Guid Id, int VersionNo, string Status, bool IsCurrent);

public sealed record WordView(string Text, int StartMs, int EndMs);

public sealed record SegmentView(Guid Id, string Speaker, Guid SpeakerId, string Language, int StartMs, int EndMs, string Text,
    bool LowConfidence, IReadOnlyList<WordView>? Words, string? OriginalText = null, bool? NewParagraph = null, string? List = null);

public sealed record VersionView(int No, string Kind, string Capacity, string? By, DateTimeOffset At, string? Note);

public sealed record TranscriptView(int VersionNo, IReadOnlyList<VersionView> Versions, IReadOnlyList<SegmentView> Segments,
    IReadOnlyList<SpeakerView> Speakers, string Engine);

public sealed record SpeakerView(Guid Id, string Label, string Name);

public sealed record OutputsView(string Engine, string Language, string Summary, IReadOnlyList<string> ActionPoints,
    string Minutes, string ToneOfMeeting);

public sealed record AudioHandle(Stream Content, string ContentType, string FileName) : IAsyncDisposable
{
    public ValueTask DisposeAsync() => Content.DisposeAsync();
}

public sealed record ReopenView(Guid Id, Guid ItemId, string ItemName, string Reason, string RequestedBy, int Approvals,
    IReadOnlyList<string> ApprovedBy, bool Open, string? Outcome, Guid? NewItemId);

public sealed record UserView(Guid Id, string Name, string Email, bool IsAdmin, bool IsSupport = false);

public sealed record DeleteRequestView(Guid Id, Guid ItemId, string ItemName, string Reason, string RequestedBy, int Approvals,
    IReadOnlyList<string> ApprovedBy, bool Open, string? Outcome);
