using System.Text.Json.Serialization;

namespace VerbaFlow.Core.Domain;

public sealed class TimelineMarker
{
    public MarkerType Type { get; init; }
    public int OffsetMs { get; init; }
    public string? Note { get; init; }
    public Guid ActorId { get; init; }
    public DateTimeOffset At { get; init; }
}

/// <summary>
/// A recorded or imported item. Holds the workflow state machine; permission checks
/// happen in the application layer before these methods are called.
/// Approved items are locked: every mutating method refuses once Status is Completed or Purged.
/// </summary>
public sealed class Item
{
    [JsonConstructor] private Item() { }

    [JsonInclude] public Guid Id { get; private set; }
    [JsonInclude] public Guid ChainId { get; private set; }
    [JsonInclude] public int VersionNo { get; private set; }
    [JsonInclude] public Guid? SupersedesItemId { get; private set; }
    /// <summary>The item whose original media this item plays (differs from Id for reopened versions).</summary>
    [JsonInclude] public Guid MediaSourceItemId { get; private set; }
    [JsonInclude] public ItemMode Mode { get; private set; }
    [JsonInclude] public SourceKind Source { get; private set; }
    [JsonInclude] public ItemStatus Status { get; private set; }
    [JsonInclude] public ProcessingState Processing { get; private set; }
    [JsonInclude] public ItemStatus? StatusBeforeFailure { get; private set; }
    [JsonInclude] public string? FailureReason { get; private set; }
    [JsonInclude] public string Name { get; private set; } = "";
    [JsonInclude] public string? ClientReference { get; private set; }
    [JsonInclude] public string? Description { get; private set; }
    [JsonInclude] public Priority Priority { get; private set; } = Priority.Normal;
    [JsonInclude] public DateOnly? DueDate { get; private set; }
    /// <summary>The author (in-app recordings) or importer (imported files).</summary>
    [JsonInclude] public Guid OwnerId { get; private set; }
    [JsonInclude] public Guid? AssignedUserId { get; private set; }
    [JsonInclude] public DateTimeOffset CreatedAt { get; private set; }
    [JsonInclude] public int LengthMs { get; private set; }
    [JsonInclude] public string OutputLanguage { get; private set; } = "en";
    /// <summary>Languages spoken in the recording, comma separated ("en", "fr" or "en,fr"). One language is faster and more accurate to transcribe than two. Items saved before this existed keep both.</summary>
    [JsonInclude] public string SpokenLanguages { get; private set; } = "en,fr";
    public void SetSpokenLanguages(string? value)
    {
        var parts = (value ?? "").Split(new[] { ',', '+' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(x => x.ToLowerInvariant()).Where(x => x is "en" or "fr").Distinct().ToList();
        SpokenLanguages = parts.Count == 0 ? "en" : string.Join(",", parts);
    }
    [JsonInclude] public bool ObjectionFlag { get; private set; }
    [JsonInclude] public DateTimeOffset? ApprovedAt { get; private set; }
    [JsonInclude] public Guid? ApprovedBy { get; private set; }
    [JsonInclude] public string? OriginalFileName { get; private set; }
    [JsonInclude] public string? ExternalSourceNote { get; private set; }
    [JsonInclude] public List<TimelineMarker> Markers { get; private set; } = [];

    /// <summary>The status where the author/importer reviews and approves.</summary>
    [JsonIgnore] public ItemStatus Home => Source == SourceKind.Imported ? ItemStatus.WithImporter : ItemStatus.WithAuthor;
    [JsonIgnore] public bool IsLocked => Status is ItemStatus.Completed or ItemStatus.Purged;
    [JsonIgnore] public bool IsSelfAssigned => AssignedUserId is { } a && a == OwnerId;

    public static Item CreateRecorded(Guid id, Guid ownerId, string name, int lengthMs, string outputLanguage,
        DateTimeOffset now) => Create(id, id, 1, null, ownerId, name, lengthMs, outputLanguage, now, SourceKind.Recorded, null, null);

    public static Item CreateImported(Guid id, Guid ownerId, string name, int lengthMs, string outputLanguage,
        DateTimeOffset now, string originalFileName, string? sourceNote) =>
        Create(id, id, 1, null, ownerId, name, lengthMs, outputLanguage, now, SourceKind.Imported, originalFileName, sourceNote);

    private static Item Create(Guid id, Guid chainId, int version, Guid? supersedes, Guid ownerId, string name,
        int lengthMs, string outputLanguage, DateTimeOffset now, SourceKind source, string? fileName, string? note)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new DomainException("A name is required.");
        var item = new Item
        {
            Id = id, ChainId = chainId, VersionNo = version, SupersedesItemId = supersedes, MediaSourceItemId = id,
            Mode = ItemMode.Meeting, Source = source, OwnerId = ownerId, Name = name.Trim(), LengthMs = lengthMs,
            OutputLanguage = outputLanguage, CreatedAt = now, OriginalFileName = fileName, ExternalSourceNote = note,
            Processing = ProcessingState.Running
        };
        item.Status = item.Home;
        return item;
    }

    public void AddMarker(TimelineMarker marker)
    {
        EnsureNotLocked();
        if (marker.Type == MarkerType.Pause && string.IsNullOrWhiteSpace(marker.Note))
            throw new DomainException("A reason is required for every pause.");
        if (marker.OffsetMs < 0) throw new DomainException("Marker offset cannot be negative.");
        Markers.Add(marker);
        if (marker.Type == MarkerType.Objection) ObjectionFlag = true;
    }

    public void UpdateDetails(string? name, string? clientReference, string? description, Priority? priority,
        DateOnly? dueDate, bool clearDueDate)
    {
        EnsureNotLocked();
        if (name is not null)
        {
            if (string.IsNullOrWhiteSpace(name)) throw new DomainException("A name is required.");
            Name = name.Trim();
        }
        if (clientReference is not null) ClientReference = clientReference.Trim();
        if (description is not null) Description = description.Trim();
        if (priority is { } p) Priority = p;
        if (clearDueDate) DueDate = null; else if (dueDate is { } d) DueDate = d;
    }

    public void SetLength(int lengthMs)
    {
        EnsureNotLocked();
        if (lengthMs < 0) throw new DomainException("Length cannot be negative.");
        LengthMs = lengthMs;
    }

    public void CompleteProcessing()
    {
        EnsureNotLocked();
        Processing = ProcessingState.Idle;
    }

    public void FailProcessing(string reason)
    {
        EnsureNotLocked();
        StatusBeforeFailure = Status == ItemStatus.ConversionFailed ? StatusBeforeFailure : Status;
        Status = ItemStatus.ConversionFailed;
        Processing = ProcessingState.Idle;
        FailureReason = reason;
    }

    public void Retry()
    {
        if (Status != ItemStatus.ConversionFailed) throw new DomainException("Only failed conversions can be retried.");
        Status = StatusBeforeFailure ?? Home;
        StatusBeforeFailure = null;
        FailureReason = null;
        Processing = ProcessingState.Running;
    }

    /// <summary>
    /// Assigns to another user (moves to Awaiting Assignee) or to the owner (stays at Home, "self-assigned").
    /// Null clears the assignment.
    /// </summary>
    public void Assign(Guid? toUserId)
    {
        EnsureNotLocked();
        if (Processing == ProcessingState.Running) throw new DomainException("The item is still being processed.");
        if (Status is ItemStatus.ConversionFailed or ItemStatus.Draft)
            throw new DomainException("The item cannot be assigned in its current state.");
        AssignedUserId = toUserId;
        Status = toUserId is { } u && u != OwnerId ? ItemStatus.AwaitingAssignee : Home;
    }

    public void OpenByAssignee(Guid userId)
    {
        EnsureNotLocked();
        if (Status != ItemStatus.AwaitingAssignee || AssignedUserId != userId)
            throw new DomainException("Only the assignee can open an item that is awaiting them.");
        Status = ItemStatus.WithAssignee;
    }

    /// <summary>Imported items return to Imported, in-app recordings to With Author.</summary>
    public void ReturnToOwner(Guid userId)
    {
        EnsureNotLocked();
        if (Status != ItemStatus.WithAssignee || AssignedUserId != userId)
            throw new DomainException("Only the assignee can return an item they are working on.");
        Status = Home;
    }

    public void Approve(Guid approverId, bool approverIsAdmin, bool blockSelfApproval, DateTimeOffset now)
    {
        EnsureNotLocked();
        if (Status != Home)
            throw new DomainException("Only items awaiting the author or importer can be approved.");
        if (Processing == ProcessingState.Running) throw new DomainException("The item is still being processed.");
        if (blockSelfApproval && IsSelfAssigned && !approverIsAdmin)
            throw new DomainException("Separation of duties: a self-assigned item needs approval by someone else.");
        Status = ItemStatus.Completed;
        ApprovedAt = now;
        ApprovedBy = approverId;
    }

    /// <summary>Creates the next linked version of a completed item. The original is never modified.</summary>
    public Item CreateReopenedVersion(Guid newId, DateTimeOffset now)
    {
        if (Status != ItemStatus.Completed) throw new DomainException("Only completed items can be reopened.");
        var next = Create(newId, ChainId, VersionNo + 1, Id, OwnerId, Name, LengthMs, OutputLanguage, now, Source,
            OriginalFileName, ExternalSourceNote);
        next.MediaSourceItemId = MediaSourceItemId;
        next.SpokenLanguages = SpokenLanguages;
        next.ClientReference = ClientReference;
        next.Description = Description;
        next.Priority = Priority;
        next.DueDate = DueDate;
        next.Markers = [.. Markers];
        next.ObjectionFlag = ObjectionFlag;
        next.Processing = ProcessingState.Idle;
        return next;
    }

    private void EnsureNotLocked()
    {
        if (IsLocked) throw new DomainException("This item is approved and locked. Request a reopen to change it.");
    }
}
