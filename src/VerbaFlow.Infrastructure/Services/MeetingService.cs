using VerbaFlow.Core.Audit;
using VerbaFlow.Core.Domain;
using VerbaFlow.Core.Providers;
using VerbaFlow.Core.Security;
using VerbaFlow.Core.Transcripts;
using VerbaFlow.Infrastructure.Media;

namespace VerbaFlow.Infrastructure.Services;

/// <summary>
/// Meeting mode: recording, import, workflow, transcript editing, reopening and audit.
/// Every method checks permission first, changes state, and appends an audit entry.
/// </summary>
public sealed class MeetingService(Stores stores, IMalwareScanner scanner, ProcessingQueue queue, PlatformPolicy policy,
    TimeProvider clock, IAiOutputService ai, FailureReporter reporter)
{
    private const string Product = "speak";

    private static readonly HashSet<string> MediaExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".mp3", ".wav", ".m4a", ".mp4", ".webm", ".ogg", ".opus", ".mov", ".aac", ".flac", ".mkv", ".wma" };

    // ---------- capture ----------

    public async Task<Guid> CreateRecordingAsync(User actor, RecordingUpload up, CancellationToken ct = default)
    {
        // A dictation is one person speaking, so there is nobody else to tell and no consent notice or markers.
        var dictation = up.Mode is ItemMode.Dictate or ItemMode.Speech;
        if (!dictation && !up.ConsentNoticeGiven)
            throw new DomainException("Confirm that everyone was told the meeting is being recorded before saving.");
        if (up.LengthMs <= 0) throw new DomainException("The recording has no length.");
        if (dictation) CheckDictation(up.Name, up.DocumentType);

        var staged = await StageAndScanAsync(actor, up.Audio, up.FileName, "recording", ct);
        var id = Guid.NewGuid();
        var now = clock.GetUtcNow();
        var item = Item.CreateRecorded(id, actor.Id, up.Name, up.LengthMs, up.OutputLanguage, now, up.Mode);
        if (!string.IsNullOrWhiteSpace(up.ClientReference)) item.UpdateDetails(null, up.ClientReference, null, null, null, false);
        if (dictation) item.SetDocumentType(up.DocumentType);
        item.SetSpokenLanguages(up.SpokenLanguages ?? up.OutputLanguage);
        if (!dictation)
        {
            ApplySpeakerCount(item, up.NumSpeakers);
            item.AddMarker(new TimelineMarker { Type = MarkerType.ConsentNotice, OffsetMs = 0, Note = "Notice given before recording", ActorId = actor.Id, At = now });
            foreach (var m in up.Markers.OrderBy(m => m.OffsetMs))
                item.AddMarker(new TimelineMarker { Type = m.Type, OffsetMs = m.OffsetMs, Note = m.Note, ActorId = actor.Id, At = now });
        }

        var asset = await stores.Media.CommitOriginalAsync(staged, id, SafeName(up.FileName), up.ContentType, "Recorded in app");
        // Speech mode: the text heard live is kept with the item before it is queued, so the conversion can use it.
        var live = up.Mode == ItemMode.Speech ? up.Live?.Text is { } heard ? heard with { ItemId = id } : null : null;
        if (live is not null) await stores.LiveText.UpsertAsync(id, live);
        await stores.Items.UpsertAsync(id, item);
        await Audit(actor, "author", id, "item.created", ("source", "Recorded in app"), ("sha256", asset.Sha256),
            ("sizeBytes", asset.SizeBytes), ("lengthMs", up.LengthMs), ("mode", up.Mode.ToString()), ("consentNoticeConfirmed", dictation ? null : true));
        if (up.Mode == ItemMode.Speech) await AuditLiveAsync(actor, id, live, up.Live, up.LengthMs);
        foreach (var m in item.Markers.Where(m => m.Type is MarkerType.Objection or MarkerType.Pause))
            await Audit(actor, "author", id, m.Type == MarkerType.Objection ? "recording.objection_logged" : "recording.paused",
                ("offsetMs", m.OffsetMs), ("note", m.Note));
        queue.Enqueue(id);
        return id;
    }

    // ---------- unfinished dictations ----------

    /// <summary>
    /// Saves a dictation unfinished, to carry on later. The first save creates it (with name, type and reference); later saves
    /// add a new audio file each time. Nothing is converted to text until the author finishes it. Only the author can save.
    /// </summary>
    public async Task<Guid> SaveDraftAsync(User actor, Guid? id, DraftUpload up, CancellationToken ct = default)
    {
        Item item;
        if (id is { } existing)
        {
            item = await stores.RequireItemAsync(existing);
            RequireDraftAuthor(actor, item);
        }
        else
        {
            CheckDictation(up.Name, up.DocumentType);
            if (up.LengthMs <= 0) throw new DomainException("The dictation has no length.");
            item = Item.CreateDraft(Guid.NewGuid(), actor.Id, up.Name!, up.LengthMs, up.OutputLanguage, clock.GetUtcNow(), up.Mode);
            if (!string.IsNullOrWhiteSpace(up.ClientReference)) item.UpdateDetails(null, up.ClientReference, null, null, null, false);
            item.SetDocumentType(up.DocumentType);
            item.SetSpokenLanguages(up.SpokenLanguages ?? up.OutputLanguage);
        }
        var version = item.SaveDraft(up.LengthMs);
        var staged = await StageAndScanAsync(actor, up.Audio, up.FileName, "recording", ct);
        var asset = await stores.Media.CommitDraftAsync(staged, item.Id, version, SafeName(up.FileName), up.ContentType);
        await KeepLiveTextAsync(item, up.Live);
        await stores.Items.UpsertAsync(item.Id, item);
        if (id is null) await Audit(actor, "author", item.Id, "item.created", ("source", "Recorded in app"), ("mode", item.Mode.ToString()), ("unfinished", true));
        await Audit(actor, "author", item.Id, "draft.saved", ("save", version), ("sha256", asset.Sha256), ("sizeBytes", asset.SizeBytes), ("lengthMs", up.LengthMs));
        return item.Id;
    }

    /// <summary>The author has finished an unfinished dictation: its final audio becomes the recording and is converted to text.</summary>
    public async Task FinishDraftAsync(User actor, Guid id, DraftUpload up, CancellationToken ct = default)
    {
        var item = await stores.RequireItemAsync(id);
        RequireDraftAuthor(actor, item);
        item.FinishDraft(up.LengthMs);
        var staged = await StageAndScanAsync(actor, up.Audio, up.FileName, "recording", ct);
        var asset = await stores.Media.CommitOriginalAsync(staged, id, SafeName(up.FileName), up.ContentType, "Recorded in app");
        var live = await KeepLiveTextAsync(item, up.Live);
        await stores.Items.UpsertAsync(id, item);
        await Audit(actor, "author", id, "draft.finished", ("savesBefore", item.DraftVersion), ("sha256", asset.Sha256),
            ("sizeBytes", asset.SizeBytes), ("lengthMs", up.LengthMs));
        if (item.Mode == ItemMode.Speech) await AuditLiveAsync(actor, id, live, up.Live, up.LengthMs);
        queue.Enqueue(id);
    }

    /// <summary>
    /// The author changes the recording of a dictation that is with them and not yet approved (more put in, or part recorded
    /// over). The new recording is kept alongside the old ones and the dictation is converted to text again, replacing
    /// saved corrections only when the author agreed.
    /// </summary>
    public async Task ChangeRecordingAsync(User actor, Guid id, DraftUpload up, bool discardEdits, CancellationToken ct = default)
    {
        var item = await stores.RequireItemAsync(id);
        if (item.OwnerId != actor.Id) throw new ForbiddenException("Only the person who dictated this can change its recording.");
        if (await RecordingRemovedAsync(item) is { } removed) throw new DomainException(removed);
        if ((await stores.Items.ListAsync()).Any(i => i.SupersedesItemId == id)) throw new DomainException("A newer version of this dictation exists.");
        var transcript = await stores.Transcripts.GetAsync(id);
        var edits = transcript is null ? 0 : transcript.PersonVersions;
        if (edits > 0 && !discardEdits)
            throw new DomainException("This text has saved corrections, and converting the changed recording would replace them.");
        var previous = await stores.Media.FindCurrentAsync(item.MediaSourceItemId);
        var revision = item.ReplaceRecording(up.LengthMs);
        var staged = await StageAndScanAsync(actor, up.Audio, up.FileName, "recording", ct);
        var asset = await stores.Media.CommitRevisionAsync(staged, id, revision, SafeName(up.FileName), up.ContentType);
        await stores.Items.UpsertAsync(id, item);
        await Audit(actor, "author", id, "recording.changed", ("change", revision), ("sha256", asset.Sha256), ("previousSha256", previous?.Sha256),
            ("sizeBytes", asset.SizeBytes), ("lengthMs", up.LengthMs), ("replacedEdits", edits));
        queue.Enqueue(id);
    }

    /// <summary>
    /// Speech mode: keeps the text heard live with the item, so its conversion (and carrying on later) can use it. Without new
    /// live text (the session had ended) the text saved before stays; whether it covers the recording is checked on conversion.
    /// </summary>
    private async Task<LiveTranscript?> KeepLiveTextAsync(Item item, LiveUpload? live)
    {
        if (item.Mode != ItemMode.Speech) return null;
        if (live?.Text is { } heard)
        {
            var t = heard with { ItemId = item.Id };
            await stores.LiveText.UpsertAsync(item.Id, t);
            return t;
        }
        return await stores.LiveText.GetAsync(item.Id);
    }

    private Task AuditLiveAsync(User actor, Guid id, LiveTranscript? live, LiveUpload? up, int lengthMs) =>
        Audit(actor, "author", id, "speech.live", ("liveEngine", live?.Engine), ("livePhrases", live?.Phrases.Count),
            ("liveFinished", live?.Finished), ("liveCoveredMs", live?.CoveredMs), ("reconnects", live?.Reconnects), ("liveFailure", live?.FailureReference),
            ("liveText", live is null ? "none (the live session had ended)" : live.Problem(lengthMs) is { } why ? $"not usable: {why}" : "complete"),
            ("textDelayMedianMs", up?.DelayMedianMs), ("textDelayWorstMs", up?.DelayWorstMs));

    private static void RequireDraftAuthor(User actor, Item item)
    {
        if (item.OwnerId != actor.Id) throw new ForbiddenException("Only the person who dictated this can carry on with it.");
        if (item.Status != ItemStatus.Draft) throw new DomainException("This dictation has already been sent to be converted to text, so it can no longer be added to.");
    }

    public async Task<Guid> ImportAsync(User actor, ImportUpload up, CancellationToken ct = default)
    {
        if (!up.RightsConfirmed)
            throw new DomainException("Confirm that you have the right to process this file before importing.");
        var ext = Path.GetExtension(up.FileName);
        if (!MediaExtensions.Contains(ext) && !(up.ContentType.StartsWith("audio/") || up.ContentType.StartsWith("video/")))
            throw new DomainException("Only audio and video files can be imported. Attach documents to an existing item instead.");
        if (up.Mode == ItemMode.Speech) throw new DomainException("Speech mode is for speaking live. Import a recording as a dictation instead.");
        if (up.Mode == ItemMode.Dictate) CheckDictation(up.Name, up.DocumentType);

        var staged = await StageAndScanAsync(actor, up.File, up.FileName, "import", ct);
        var id = Guid.NewGuid();
        var name = string.IsNullOrWhiteSpace(up.Name) ? Path.GetFileNameWithoutExtension(up.FileName) : up.Name!;
        var item = Item.CreateImported(id, actor.Id, name, up.LengthMs, up.OutputLanguage, clock.GetUtcNow(), SafeName(up.FileName), up.SourceNote, up.Mode);
        if (!string.IsNullOrWhiteSpace(up.ClientReference)) item.UpdateDetails(null, up.ClientReference, null, null, null, false);
        if (up.Mode == ItemMode.Dictate) item.SetDocumentType(up.DocumentType);
        item.SetSpokenLanguages(up.SpokenLanguages ?? up.OutputLanguage);
        if (up.Mode != ItemMode.Dictate) ApplySpeakerCount(item, up.NumSpeakers);
        var asset = await stores.Media.CommitOriginalAsync(staged, id, SafeName(up.FileName), up.ContentType, "Imported");
        await stores.Items.UpsertAsync(id, item);
        await Audit(actor, "importer", id, "item.imported", ("source", "Imported (external)"), ("originalFileName", SafeName(up.FileName)),
            ("claimedContentType", up.ContentType), ("mode", up.Mode.ToString()), ("sha256", asset.Sha256), ("sizeBytes", asset.SizeBytes),
            ("rightsConfirmed", true), ("sourceNote", up.SourceNote));
        queue.Enqueue(id);
        return id;
    }

    /// <summary>Longest online meeting video kept: a few hours of meeting video is far bigger than an upload.</summary>
    public const long MaxMeetingVideoBytes = 8L * 1024 * 1024 * 1024;

    /// <summary>
    /// Meet: the video Meet's note-taker recorded becomes a Meeting item for the person who booked it first; colleagues who
    /// asked for the same meeting can read it. Who the platform said was speaking is kept, so speakers get people's names.
    /// </summary>
    public async Task<Guid> CreateFromOnlineMeetingAsync(OnlineMeetingUpload up, CancellationToken ct = default)
    {
        var owner = await stores.RequireUserAsync(up.Requesters[0]);
        var staged = await StageAndScanAsync(owner, up.Video, up.FileName, "online-meeting", ct, MaxMeetingVideoBytes);
        var id = Guid.NewGuid();
        var now = clock.GetUtcNow();
        var note = $"{up.Platform} meeting, recorded by the note-taker {up.NoteTakerName}";
        var item = Item.CreateFromOnlineMeeting(id, owner.Id, up.Requesters.Skip(1), up.Name, now, note, SafeName(up.FileName));
        item.SetSpokenLanguages(up.SpokenLanguages);
        item.AddMarker(new TimelineMarker { Type = MarkerType.ConsentNotice, OffsetMs = 0, ActorId = owner.Id, At = now,
            Note = $"The note-taker joined as a visible participant named {up.NoteTakerName} and announced the recording in the meeting chat" });
        var asset = await stores.Media.CommitOriginalAsync(staged, id, SafeName(up.FileName), up.ContentType, note);
        if (up.Speakers.Count > 0) await stores.MeetingSpeakers.UpsertAsync(id, new MeetingSpeakers(id, up.Speakers));
        await stores.Items.UpsertAsync(id, item);
        await stores.Audit.AppendAsync(null, "system", Product, id, "item.created", AuditChain.Details(("source", "Online meeting (note-taker)"),
            ("platform", up.Platform), ("noteTaker", up.NoteTakerId), ("owner", owner.Id), ("attendees", item.Attendees.Count),
            ("sha256", asset.Sha256), ("sizeBytes", asset.SizeBytes), ("contentType", up.ContentType), ("platformSpeakerStretches", up.Speakers.Count),
            ("platformSpeakers", up.Speakers.Select(x => x.Participant).Distinct().Count())));
        queue.Enqueue(id);
        return id;
    }

    public async Task RetryAsync(User actor, Guid id, string? spokenLanguages = null)
    {
        var item = await stores.RequireItemAsync(id);
        RequireOwnerOrAdmin(actor, item);
        item.Retry();
        if (!string.IsNullOrWhiteSpace(spokenLanguages)) item.SetSpokenLanguages(spokenLanguages);
        await stores.Items.UpsertAsync(id, item);
        await Audit(actor, CapacityOf(actor, item), id, "processing.retry_requested");
        queue.Enqueue(id);
    }

    /// <summary>A dictation needs a name (a client reference alone is not enough) and to say whether it is a letter, Word document or email.</summary>
    private static void CheckDictation(string? name, string? documentType)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new DomainException("Give the dictation a name before starting.");
        if (documentType is null || !Item.DocumentTypes.Contains(documentType.Trim().ToLowerInvariant()))
            throw new DomainException("Choose whether this is a letter, a Word document or an email.");
    }

    /// <summary>When the person says how many people spoke, that count goes to the speech service and its own speaker labels are used.</summary>
    private static void ApplySpeakerCount(Item item, int? count)
    {
        if (count is null) return;
        item.SetNumSpeakers(count);
        item.SetSpeakerMethod("azure");
    }

    /// <summary>Runs the conversion again on the same audio, optionally telling it how many people spoke. Only while the transcript has no human edits.</summary>
    public async Task RerunAsync(User actor, Guid id, int? numSpeakers, string? spokenLanguages = null, bool discardEdits = false, string? method = null, string? audio = null,
        string? dictationService = null)
    {
        var item = await stores.RequireItemAsync(id);
        RequireOwnerOrAdmin(actor, item);
        if (await RecordingRemovedAsync(item) is { } removed) throw new DomainException(removed + " So it cannot be converted again.");
        var transcript = await stores.Transcripts.GetAsync(id);
        var edits = transcript is null ? 0 : transcript.PersonVersions;
        if (edits > 0 && !discardEdits)
            throw new DomainException("This transcript has been edited, and running the conversion again would replace those edits.");
        item.Reprocess();
        item.SetNumSpeakers(numSpeakers);
        item.SetSpeakerMethod(method);
        item.SetSpeechAudio(audio);
        if (item.IsDictation) item.SetDictationService(dictationService);
        if (!string.IsNullOrWhiteSpace(spokenLanguages)) item.SetSpokenLanguages(spokenLanguages);
        await stores.Items.UpsertAsync(id, item);
        await Audit(actor, CapacityOf(actor, item), id, "processing.rerun_requested", ("numSpeakers", numSpeakers), ("method", item.SpeakerMethod), ("audio", item.SpeechAudio ?? "prepared"),
            ("speechService", item.IsDictation ? item.DictationService ?? "word for word" : null), ("replacedEdits", edits));
        queue.Enqueue(id);
    }

    // ---------- workflow ----------

    public async Task UpdateDetailsAsync(User actor, Guid id, DetailsUpdate d)
    {
        var item = await stores.RequireItemAsync(id);
        PermissionEvaluator.Require(actor, item, Capability.Write);
        RequireOwnerOrAdmin(actor, item);
        item.UpdateDetails(d.Name, d.ClientReference, d.Description, d.Priority, d.DueDate, d.ClearDueDate);
        await stores.Items.UpsertAsync(id, item);
        await Audit(actor, CapacityOf(actor, item), id, "item.details_updated", ("name", item.Name),
            ("clientReference", item.ClientReference), ("description", item.Description), ("priority", item.Priority),
            ("dueDate", item.DueDate?.ToString("yyyy-MM-dd")));
    }

    public async Task AssignAsync(User actor, Guid id, Guid? toUserId, string? reason)
    {
        var item = await stores.RequireItemAsync(id);
        PermissionEvaluator.Require(actor, item, Capability.Reassign);
        User? target = null;
        if (toUserId is { } t) target = await stores.RequireUserAsync(t);
        var previous = item.AssignedUserId;
        var isReassignment = previous is not null && previous != toUserId && previous != item.OwnerId;
        if (isReassignment && string.IsNullOrWhiteSpace(reason))
            throw new DomainException("Give a reason when re-assigning an item (for example sickness or leave).");
        item.Assign(toUserId);
        await stores.Items.UpsertAsync(id, item);
        await Audit(actor, CapacityOf(actor, item), id, "item.assigned", ("from", previous), ("to", toUserId),
            ("toName", target?.Name), ("selfAssigned", item.IsSelfAssigned), ("reason", reason));
    }

    public async Task AcceptAsync(User actor, Guid id)
    {
        var item = await stores.RequireItemAsync(id);
        item.OpenByAssignee(actor.Id);
        await stores.Items.UpsertAsync(id, item);
        await Audit(actor, "assignee", id, "item.opened_by_assignee");
    }

    public async Task ReturnAsync(User actor, Guid id)
    {
        var item = await stores.RequireItemAsync(id);
        item.ReturnToOwner(actor.Id);
        await stores.Items.UpsertAsync(id, item);
        await Audit(actor, "assignee", id, "item.returned_to_owner", ("returnedTo", item.Status));
    }

    public async Task ApproveAsync(User actor, Guid id)
    {
        var item = await stores.RequireItemAsync(id);
        PermissionEvaluator.Require(actor, item, Capability.Approve);
        var selfReview = item.IsSelfAssigned || item.AssignedUserId is null && item.OwnerId == actor.Id;
        item.Approve(actor.Id, actor.IsAdmin, policy.BlockSelfApproval, clock.GetUtcNow());
        var asset = await stores.Media.FindCurrentAsync(item.MediaSourceItemId);
        var transcript = await stores.Transcripts.GetAsync(id);
        await stores.Items.UpsertAsync(id, item);
        await Audit(actor, CapacityOf(actor, item), id, "item.approved", ("selfReview", selfReview),
            ("audioSha256", asset?.Sha256), ("transcriptVersion", transcript?.Current.No));
    }

    // ---------- reopen ----------

    public async Task<Guid> RequestReopenAsync(User actor, Guid itemId, string reason)
    {
        var item = await stores.RequireItemAsync(itemId);
        RequireOwnerOrAdmin(actor, item);
        if (item.Status != ItemStatus.Completed) throw new DomainException("Only completed items can be reopened.");
        if ((await stores.Items.ListAsync()).Any(i => i.SupersedesItemId == itemId))
            throw new DomainException("This version has already been reopened. Open the newer version instead.");
        if ((await stores.Reopen.ListAsync()).Any(r => r.ItemId == itemId && r.IsOpen))
            throw new DomainException("A reopen request is already waiting for approval.");
        var req = ReopenRequest.Create(Guid.NewGuid(), itemId, actor.Id, reason, clock.GetUtcNow());
        await stores.Reopen.UpsertAsync(req.Id, req);
        await Audit(actor, CapacityOf(actor, item), null, "reopen.requested", ("requestId", req.Id), ("originalItemId", itemId), ("reason", req.Reason));
        return req.Id;
    }

    /// <summary>Records one admin's approval. The second distinct approval creates the new linked version.</summary>
    public async Task<Guid?> ApproveReopenAsync(User admin, Guid requestId)
    {
        var req = await stores.Reopen.GetAsync(requestId) ?? throw new NotFoundException("That request does not exist.");
        req.Approve(admin);
        await Audit(admin, "admin", null, "reopen.approved", ("requestId", requestId), ("originalItemId", req.ItemId));
        if (!req.IsApproved) { await stores.Reopen.UpsertAsync(requestId, req); return null; }

        var original = await stores.RequireItemAsync(req.ItemId);
        var now = clock.GetUtcNow();
        var next = original.CreateReopenedVersion(Guid.NewGuid(), now);
        var transcript = await stores.Transcripts.GetAsync(original.Id);
        if (transcript is not null) await stores.Transcripts.UpsertAsync(next.Id, transcript.ForkFor(next.Id, admin.Id, now));
        var outputs = await stores.Outputs.GetAsync(original.Id);
        if (outputs is not null) await stores.Outputs.UpsertAsync(next.Id, outputs with { ItemId = next.Id });
        await stores.Items.UpsertAsync(next.Id, next);
        req.NewItemId = next.Id;
        await stores.Reopen.UpsertAsync(requestId, req);

        var approvers = string.Join(",", req.Approvals);
        await Audit(admin, "admin", next.Id, "item.reopened", ("requestId", requestId), ("reason", req.Reason),
            ("requestedBy", req.RequestedBy), ("approvedBy", approvers), ("supersedes", original.Id), ("versionNo", next.VersionNo));
        await Audit(admin, "system", original.Id, "item.reopened_version_created", ("newItemId", next.Id), ("versionNo", next.VersionNo));
        return next.Id;
    }

    public async Task RejectReopenAsync(User admin, Guid requestId, string reason)
    {
        var req = await stores.Reopen.GetAsync(requestId) ?? throw new NotFoundException("That request does not exist.");
        req.Reject(admin, reason);
        await stores.Reopen.UpsertAsync(requestId, req);
        await Audit(admin, "admin", null, "reopen.rejected", ("requestId", requestId), ("originalItemId", req.ItemId), ("reason", reason));
    }

    public async Task<IReadOnlyList<ReopenView>> ListReopenRequestsAsync(User actor)
    {
        if (!actor.IsAdmin) throw new ForbiddenException("Only administrators can see reopen requests.");
        var users = (await stores.Users.ListAsync()).ToDictionary(u => u.Id);
        var items = (await stores.Items.ListAsync()).ToDictionary(i => i.Id);
        return (await stores.Reopen.ListAsync()).OrderByDescending(r => r.CreatedAt).Select(r => new ReopenView(
            r.Id, r.ItemId, items.GetValueOrDefault(r.ItemId)?.Name ?? "?", r.Reason,
            users.GetValueOrDefault(r.RequestedBy)?.Name ?? "?", r.Approvals.Count,
            r.Approvals.Select(a => users.GetValueOrDefault(a)?.Name ?? "?").ToList(), r.IsOpen,
            r.RejectedBy is not null ? $"Rejected: {r.RejectionReason}" : r.NewItemId is not null ? "Reopened" : null, r.NewItemId)).ToList();
    }

    // ---------- delete ----------

    /// <summary>
    /// Deletes a recording that has never been approved: the author, importer or an administrator can do it at once.
    /// Its audio, transcript and summary are removed; the item stays as a tombstone in the Deleted view and its history is kept.
    /// </summary>
    public async Task DeleteAsync(User actor, Guid id, string reason)
    {
        var item = await stores.RequireItemAsync(id);
        RequireOwnerOrAdmin(actor, item);
        if (string.IsNullOrWhiteSpace(reason)) throw new DomainException("A reason is required to delete a recording.");
        var chain = await RequireDeletableAsync(item);
        if (chain.Any(i => i.ApprovedAt is not null))
            throw new DomainException("This recording was approved before, so two administrators must agree to delete it. Use Ask to delete instead.");
        await PurgeChainAsync(actor, chain, reason.Trim(), null, [actor.Id]);
    }

    /// <summary>Asks to delete a recording that was approved before and has since been reopened. Two different administrators must agree.</summary>
    public async Task<Guid> RequestDeleteAsync(User actor, Guid id, string reason)
    {
        var item = await stores.RequireItemAsync(id);
        RequireOwnerOrAdmin(actor, item);
        var chain = await RequireDeletableAsync(item);
        if (chain.All(i => i.ApprovedAt is null))
            throw new DomainException("This recording has never been approved, so it can be deleted straight away.");
        var ids = chain.Select(i => i.Id).ToHashSet();
        if ((await stores.Deletes.ListAsync()).Any(r => ids.Contains(r.ItemId) && r.IsOpen))
            throw new DomainException("A request to delete this recording is already waiting for approval.");
        var req = DeleteRequest.Create(Guid.NewGuid(), id, actor, reason, clock.GetUtcNow());
        await stores.Deletes.UpsertAsync(req.Id, req);
        await Audit(actor, CapacityOf(actor, item), id, "delete.requested", ("requestId", req.Id), ("reason", req.Reason),
            ("approvals", req.Approvals.Count));
        return req.Id;
    }

    /// <summary>Records an administrator's agreement. The second different administrator's agreement deletes the recording.</summary>
    public async Task<bool> ApproveDeleteAsync(User admin, Guid requestId)
    {
        var req = await stores.Deletes.GetAsync(requestId) ?? throw new NotFoundException("That request does not exist.");
        req.Approve(admin);
        if (req.IsApproved)
        {
            var item = await stores.RequireItemAsync(req.ItemId);
            var chain = await RequireDeletableAsync(item); // still the newest version, not approved again, not processing
            await stores.Deletes.UpsertAsync(requestId, req);
            await Audit(admin, "admin", req.ItemId, "delete.approved", ("requestId", requestId));
            await PurgeChainAsync(admin, chain, req.Reason, requestId, req.Approvals);
            req.DeletedAt = clock.GetUtcNow();
            await stores.Deletes.UpsertAsync(requestId, req);
            return true;
        }
        await stores.Deletes.UpsertAsync(requestId, req);
        await Audit(admin, "admin", req.ItemId, "delete.approved", ("requestId", requestId));
        return false;
    }

    public async Task RejectDeleteAsync(User admin, Guid requestId, string reason)
    {
        var req = await stores.Deletes.GetAsync(requestId) ?? throw new NotFoundException("That request does not exist.");
        req.Reject(admin, reason);
        await stores.Deletes.UpsertAsync(requestId, req);
        await Audit(admin, "admin", req.ItemId, "delete.rejected", ("requestId", requestId), ("reason", req.RejectionReason));
    }

    public async Task<IReadOnlyList<DeleteRequestView>> ListDeleteRequestsAsync(User actor)
    {
        if (!actor.IsAdmin) throw new ForbiddenException("Only administrators can see delete requests.");
        var users = (await stores.Users.ListAsync()).ToDictionary(u => u.Id);
        var items = (await stores.Items.ListAsync()).ToDictionary(i => i.Id);
        return (await stores.Deletes.ListAsync()).OrderByDescending(r => r.CreatedAt).Select(r => new DeleteRequestView(
            r.Id, r.ItemId, items.GetValueOrDefault(r.ItemId)?.Name ?? "?", r.Reason,
            users.GetValueOrDefault(r.RequestedBy)?.Name ?? "?", r.Approvals.Count,
            r.Approvals.Select(a => users.GetValueOrDefault(a)?.Name ?? "?").ToList(), r.IsOpen,
            r.RejectedBy is not null ? $"Rejected: {r.RejectionReason}" : r.DeletedAt is not null ? "Deleted" : null)).ToList();
    }

    /// <summary>The checks every deletion makes. Returns every version of the recording, oldest first.</summary>
    private async Task<List<Item>> RequireDeletableAsync(Item item)
    {
        var all = await stores.Items.ListAsync();
        if (item.Status == ItemStatus.Purged) throw new DomainException("This recording has already been deleted.");
        if (all.Any(i => i.SupersedesItemId == item.Id)) throw new DomainException("A newer version of this recording exists. Delete it from the newest version.");
        if (item.Status == ItemStatus.Completed)
            throw new DomainException("This recording is approved and locked. Ask for it to be reopened first, then delete the reopened version.");
        if (item.Processing == ProcessingState.Running) throw new DomainException("The item is still being processed. Wait for it to finish, then delete it.");
        return all.Where(i => i.ChainId == item.ChainId).OrderBy(i => i.VersionNo).ToList();
    }

    /// <summary>Deletes the audio, transcripts and summaries of every version and marks each version deleted. The history is kept.</summary>
    /// <param name="actor">Null when the retention rules delete it (recorded in History as the system).</param>
    private async Task PurgeChainAsync(User? actor, List<Item> chain, string reason, Guid? requestId, IReadOnlyList<Guid> agreedBy)
    {
        var now = clock.GetUtcNow();
        var users = (await stores.Users.ListAsync()).ToDictionary(u => u.Id, u => u.Name);
        int removedFiles = 0, stuckFiles = 0;
        foreach (var source in chain.Select(i => i.MediaSourceItemId).Distinct())
        {
            var (removed, failed) = await stores.Media.DeleteContentAsync(source);
            removedFiles += removed;
            stuckFiles += failed;
        }
        var attachments = await AttachmentService.DeleteForChainAsync(stores, chain[0].ChainId);
        foreach (var i in chain)
        {
            i.Purge(actor?.Id ?? RetentionActor, reason, now);
            await stores.Items.UpsertAsync(i.Id, i);
            await stores.Transcripts.DeleteAsync(i.Id);
            await stores.Outputs.DeleteAsync(i.Id);
            await stores.LiveText.DeleteAsync(i.Id);
            await stores.MeetingSpeakers.DeleteAsync(i.MediaSourceItemId);
            await stores.Audit.AppendAsync(actor?.Id, actor is null ? "system" : CapacityOf(actor, i), Product, i.Id, "item.deleted", AuditChain.Details(
                ("reason", reason), ("requestId", requestId),
                ("agreedBy", string.Join(", ", agreedBy.Select(a => users.GetValueOrDefault(a, "?")))), ("versions", chain.Count),
                ("audioFilesRemoved", removedFiles), ("audioFilesNotRemoved", stuckFiles == 0 ? null : stuckFiles),
                ("attachedFilesRemoved", attachments == 0 ? null : attachments),
                ("by", actor is null ? "retention rules" : null)));
        }
    }

    /// <summary>Who deleted an item that the retention rules deleted.</summary>
    public static readonly Guid RetentionActor = Guid.Empty;

    /// <summary>
    /// The retention rules delete every version of a recording, approved or not. The rules themselves were agreed by two
    /// administrators (see RetentionService), which stands in for the two-administrator delete of an approved recording.
    /// </summary>
    public async Task<bool> DeleteByRetentionAsync(Guid chainId, string reason, IReadOnlyList<Guid> agreedBy)
    {
        var chain = (await stores.Items.ListAsync()).Where(i => i.ChainId == chainId).OrderBy(i => i.VersionNo).ToList();
        if (chain.Count == 0 || chain.Any(i => i.Status == ItemStatus.Purged || i.Processing == ProcessingState.Running)) return false;
        await PurgeChainAsync(null, chain, reason, null, agreedBy);
        return true;
    }

    /// <summary>A plain sentence when the retention rules removed this item's recording (its text is kept); null otherwise.</summary>
    public async Task<string?> RecordingRemovedAsync(Item item) =>
        await stores.RecordingRemovals.GetAsync(item.MediaSourceItemId) is { } r
            ? $"The recording was removed on {r.At:d MMM yyyy} under the organisation's retention rules. The text is kept."
            : null;

    // ---------- transcript ----------

    public async Task<TranscriptView> GetTranscriptAsync(User actor, Guid id, int? versionNo = null)
    {
        var item = await stores.RequireItemAsync(id);
        PermissionEvaluator.Require(actor, item, Capability.Read);
        var t = await stores.Transcripts.GetAsync(id) ?? throw new NotFoundException("The transcript is not ready yet.");
        var users = (await stores.Users.ListAsync()).ToDictionary(u => u.Id, u => u.Name);
        var v = versionNo is null ? t.Current : t.Version(versionNo.Value);
        // The machine output (version 1) is the baseline for tracked changes. It is only comparable passage by passage
        // while both versions have the same number of passages; edits replace a passage in place, so that holds today.
        var current = t.Render(v.No);
        var machine = t.Render(1);
        string? OriginalOf(int i) => machine.Count == current.Count && machine[i].Segment.Text != current[i].Segment.Text
            ? machine[i].Segment.Text : null;
        return new TranscriptView(v.No,
            t.Versions.Select(x => new VersionView(x.No, x.Kind.ToString(), x.Capacity,
                x.CreatedBy is { } u ? users.GetValueOrDefault(u) : null, x.CreatedAt, x.Note)).ToList(),
            current.Select((r, i) => new SegmentView(r.Segment.Id, r.SpeakerName, r.Segment.SpeakerId, r.Segment.Language,
                r.Segment.StartMs, r.Segment.EndMs, r.Segment.Text, r.Segment.LowConfidence,
                r.Segment.Words?.Select(w => new WordView(w.Text, w.StartMs, w.EndMs)).ToList(), OriginalOf(i), r.Segment.NewParagraph, r.Segment.List)).ToList(),
            t.Speakers.Select(s => new SpeakerView(s.Id, s.Label, v.SpeakerNames.GetValueOrDefault(s.Id, s.Label))).ToList(), t.Engine);
    }

    public async Task EditSegmentAsync(User actor, Guid id, Guid segmentId, string text)
    {
        var item = await stores.RequireItemAsync(id);
        PermissionEvaluator.Require(actor, item, Capability.Write);
        var t = await stores.Transcripts.GetAsync(id) ?? throw new NotFoundException("The transcript is not ready yet.");
        var v = t.EditSegment(segmentId, text, actor.Id, CapacityOf(actor, item), clock.GetUtcNow());
        await stores.Transcripts.UpsertAsync(id, t);
        await Audit(actor, v.Capacity, id, "transcript.edited", ("version", v.No), ("segmentId", segmentId));
    }

    public async Task JoinParagraphAsync(User actor, Guid id, Guid segmentId)
    {
        var item = await stores.RequireItemAsync(id);
        PermissionEvaluator.Require(actor, item, Capability.Write);
        var t = await stores.Transcripts.GetAsync(id) ?? throw new NotFoundException("The transcript is not ready yet.");
        var v = t.JoinWithPrevious(segmentId, actor.Id, CapacityOf(actor, item), clock.GetUtcNow());
        await stores.Transcripts.UpsertAsync(id, t);
        await Audit(actor, v.Capacity, id, "transcript.paragraphs_joined", ("version", v.No), ("segmentId", segmentId));
    }

    public async Task RenameSpeakerAsync(User actor, Guid id, Guid speakerId, string name)
    {
        var item = await stores.RequireItemAsync(id);
        PermissionEvaluator.Require(actor, item, Capability.Write);
        var t = await stores.Transcripts.GetAsync(id) ?? throw new NotFoundException("The transcript is not ready yet.");
        var v = t.RenameSpeaker(speakerId, name, actor.Id, CapacityOf(actor, item), clock.GetUtcNow());
        await stores.Transcripts.UpsertAsync(id, t);
        await Audit(actor, v.Capacity, id, "transcript.speaker_renamed", ("version", v.No), ("speakerId", speakerId), ("name", name.Trim()));
    }

    public async Task RestoreVersionAsync(User actor, Guid id, int versionNo)
    {
        var item = await stores.RequireItemAsync(id);
        PermissionEvaluator.Require(actor, item, Capability.Write);
        var t = await stores.Transcripts.GetAsync(id) ?? throw new NotFoundException("The transcript is not ready yet.");
        var v = t.Restore(versionNo, actor.Id, CapacityOf(actor, item), clock.GetUtcNow());
        await stores.Transcripts.UpsertAsync(id, t);
        await Audit(actor, v.Capacity, id, "transcript.restored", ("restoredFrom", versionNo), ("version", v.No));
    }

    /// <summary>
    /// Writes the summary, minutes, actions and tone again from the transcript as it is now, with its speaker names.
    /// Use it after correcting the transcript or naming the speakers. Locked items cannot be changed.
    /// </summary>
    public async Task RegenerateOutputsAsync(User actor, Guid id, CancellationToken ct = default)
    {
        var item = await stores.RequireItemAsync(id);
        PermissionEvaluator.Require(actor, item, Capability.Write);
        if (item.Processing == ProcessingState.Running) throw new DomainException("Wait for transcription to finish first.");
        var t = await stores.Transcripts.GetAsync(id) ?? throw new NotFoundException("The transcript is not ready yet.");
        AiOutputs outputs;
        try { outputs = await ai.GenerateAsync(t.Render(), item.OutputLanguage, ct); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var rep = await reporter.ReportAsync("summary", id, ex);
            await Audit(actor, CapacityOf(actor, item), id, "outputs.failed", ("kind", rep.Kind.ToString()), ("reference", rep.Reference));
            throw new DomainException(rep.UserMessage);
        }
        await stores.Outputs.UpsertAsync(id, new StoredOutputs(id, outputs));
        await Audit(actor, CapacityOf(actor, item), id, "outputs.regenerated", ("engine", outputs.Engine), ("transcriptVersion", t.Current.No));
    }

    public async Task<OutputsView?> GetOutputsAsync(User actor, Guid id)
    {
        var item = await stores.RequireItemAsync(id);
        PermissionEvaluator.Require(actor, item, Capability.Read);
        var o = await stores.Outputs.GetAsync(id);
        return o is null ? null : new OutputsView(o.Outputs.Engine, o.Outputs.Language, o.Outputs.Summary, o.Outputs.ActionPoints,
            o.Outputs.Minutes, o.Outputs.ToneOfMeeting);
    }

    // ---------- audio ----------

    /// <summary>Playback needs read access. Downloading the audio is a separate, tighter capability.</summary>
    public async Task<AudioHandle> OpenAudioAsync(User actor, Guid id, bool download)
    {
        var item = await stores.RequireItemAsync(id);
        PermissionEvaluator.Require(actor, item, download ? Capability.ExportAudio : Capability.Read);
        if (item.Status == ItemStatus.Purged) throw new NotFoundException("This recording has been deleted.");
        if (await RecordingRemovedAsync(item) is { } removed) throw new NotFoundException(removed);
        var asset = await PlayableAsync(item) ?? throw new NotFoundException("The recording is missing.");
        await Audit(actor, CapacityOf(actor, item), id, download ? "audio.downloaded" : "audio.played", ("sha256", asset.Sha256));
        return new AudioHandle(stores.Media.OpenRead(asset), asset.ContentType, asset.FileName);
    }

    /// <summary>
    /// Playing through a link (a meeting video is too big to load whole): the right to read is checked and the playing
    /// recorded once here; the link then streams the file (see OpenLinkedAsync).
    /// </summary>
    public async Task StartPlaybackAsync(User actor, Guid id)
    {
        var item = await stores.RequireItemAsync(id);
        PermissionEvaluator.Require(actor, item, Capability.Read);
        if (item.Status == ItemStatus.Purged) throw new NotFoundException("This recording has been deleted.");
        if (await RecordingRemovedAsync(item) is { } removed) throw new NotFoundException(removed);
        var asset = await PlayableAsync(item) ?? throw new NotFoundException("The recording is missing.");
        await Audit(actor, CapacityOf(actor, item), id, "audio.played", ("sha256", asset.Sha256), ("by", "link"));
    }

    /// <summary>The recording for a playback link already checked by StartPlaybackAsync. Refused once the item is deleted.</summary>
    public async Task<AudioHandle> OpenLinkedAsync(Guid id)
    {
        var item = await stores.RequireItemAsync(id);
        if (item.Status == ItemStatus.Purged) throw new NotFoundException("This recording has been deleted.");
        if (await RecordingRemovedAsync(item) is { } removed) throw new NotFoundException(removed);
        var asset = await PlayableAsync(item) ?? throw new NotFoundException("The recording is missing.");
        return new AudioHandle(stores.Media.OpenRead(asset), asset.ContentType, asset.FileName);
    }

    /// <summary>The recording, or for an unfinished dictation its newest save.</summary>
    private async Task<MediaAsset?> PlayableAsync(Item item) =>
        await stores.Media.FindCurrentAsync(item.MediaSourceItemId)
        ?? (item.Status == ItemStatus.Draft ? await stores.Media.FindLatestDraftAsync(item.Id) : null);

    // ---------- dashboard ----------

    public async Task<IReadOnlyList<ItemRow>> ListAsync(User actor, ListQuery q)
    {
        var all = await stores.Items.ListAsync();
        var users = (await stores.Users.ListAsync()).ToDictionary(u => u.Id);
        var superseded = all.Where(i => i.SupersedesItemId is not null).Select(i => i.SupersedesItemId!.Value).ToHashSet();
        var files = await AttachedCountsAsync();
        var rows = new List<ItemRow>();
        foreach (var i in all.Where(i => PermissionEvaluator.CanSee(actor, i)))
        {
            if (q.Mode is { } m && i.Mode != m) continue;
            if (q.Statuses is not null && !q.Statuses.Contains(i.Status)) continue;
            if (q.SelfAssigned is { } sa && i.IsSelfAssigned != sa) continue;
            if (!string.IsNullOrWhiteSpace(q.Search) &&
                !$"{i.Name} {i.ClientReference} {i.Description}".Contains(q.Search, StringComparison.OrdinalIgnoreCase)) continue;
            rows.Add(ToRow(actor, i, users, superseded.Contains(i.Id), files));
        }
        return rows.OrderByDescending(r => r.CreatedOn).ToList();
    }

    public async Task<ItemDetail> GetItemAsync(User actor, Guid id)
    {
        var item = await stores.RequireItemAsync(id);
        PermissionEvaluator.Require(actor, item, Capability.Read);
        var all = await stores.Items.ListAsync();
        var users = (await stores.Users.ListAsync()).ToDictionary(u => u.Id);
        var superseded = all.Any(i => i.SupersedesItemId == id);
        var asset = await PlayableAsync(item);
        var chain = all.Where(i => i.ChainId == item.ChainId).OrderBy(i => i.VersionNo).ToList();
        var role = PermissionEvaluator.RoleOn(actor, item);
        var ownerRole = item.Source == SourceKind.Imported ? "Importer" : "Author";
        var removed = await RecordingRemovedAsync(item);
        var row = ToRow(actor, item, users, superseded, await AttachedCountsAsync());
        if (removed is not null) row = row with { Actions = row.Actions.Where(a => a is not ("rerun" or "change-recording" or "retry")).ToList() };
        return new ItemDetail(row, users.GetValueOrDefault(item.OwnerId)?.Name ?? "?", ownerRole,
            item.Processing == ProcessingState.Running ? "Processing" : null, item.FailureReason, item.OriginalFileName,
            item.ExternalSourceNote, asset?.Sha256,
            item.Markers.Select(m => new MarkerView(m.Type.ToString(), m.OffsetMs, m.Note, users.GetValueOrDefault(m.ActorId)?.Name ?? "?", m.At)).ToList(),
            PermissionEvaluator.Evaluate(actor, item, Capability.Write).Allowed,
            PermissionEvaluator.Evaluate(actor, item, Capability.Write).Allowed && (role == ItemRole.Owner || actor.IsAdmin),
            PermissionEvaluator.Evaluate(actor, item, Capability.ExportAudio).Allowed,
            actor.IsAdmin && role == ItemRole.None ? "Admin" : role.ToString(), item.OutputLanguage, item.ApprovedAt,
            item.ApprovedBy is { } a ? users.GetValueOrDefault(a)?.Name : null, item.ChainId,
            chain.Select(c => new ChainEntry(c.Id, c.VersionNo, StatusLabels.For(c.Mode, c.Status), !all.Any(x => x.SupersedesItemId == c.Id))).ToList(),
            item.Source switch { SourceKind.Imported => "Imported (external source)", SourceKind.OnlineMeeting => item.ExternalSourceNote ?? "Online meeting", _ => "Recorded in app" }, item.SpokenLanguages,
            item.DeletedAt, item.DeletedBy is { } db ? (db == RetentionActor ? "the retention rules" : users.GetValueOrDefault(db)?.Name) : null, item.DeleteReason,
            item.Status != ItemStatus.Purged && PermissionEvaluator.Evaluate(actor, item, Capability.Export).Allowed,
            item.Mode.ToString(), item.DocumentType, item.ClientReference, asset?.ContentType.StartsWith("video/") == true,
            item.Attendees.Select(a => users.GetValueOrDefault(a)?.Name ?? "?").ToList(), removed);
    }

    public async Task<IReadOnlyList<AuditEvent>> GetAuditAsync(User actor, Guid itemId)
    {
        var item = await stores.RequireItemAsync(itemId);
        PermissionEvaluator.Require(actor, item, Capability.Read);
        return await stores.Audit.ListAsync(itemId);
    }

    public async Task<AuditVerification> VerifyAuditAsync(User actor)
    {
        if (!actor.IsAdmin) throw new ForbiddenException("Only administrators can verify the audit trail.");
        return await stores.Audit.VerifyAsync();
    }

    public async Task<IReadOnlyList<UserView>> ListUsersAsync() =>
        (await stores.Users.ListAsync()).Select(u => new UserView(u.Id, u.Name, u.Email, u.IsAdmin, u.IsSupport)).ToList();

    // ---------- helpers ----------

    /// <summary>How many files are attached to each recording (by chain), for the paperclip in the lists.</summary>
    private async Task<Dictionary<Guid, int>> AttachedCountsAsync() =>
        (await stores.Attachments.ListAsync()).GroupBy(a => a.ChainId).ToDictionary(g => g.Key, g => g.Count());

    private ItemRow ToRow(User actor, Item i, Dictionary<Guid, User> users, bool superseded, Dictionary<Guid, int> files)
    {
        var tags = new List<string>();
        if (i.IsSelfAssigned && i.Status == i.Home) tags.Add("Self-assigned");
        if (i.Source == SourceKind.Imported) tags.Add("Imported");
        if (i.ObjectionFlag) tags.Add("Objection logged");
        if (i.Processing == ProcessingState.Running) tags.Add("Processing");
        if (superseded) tags.Add("Superseded");
        if (i.VersionNo > 1) tags.Add($"v{i.VersionNo}");
        var overdue = i.DueDate is { } d && i.Status != ItemStatus.Completed && d < DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);
        return new ItemRow(i.Id, i.Name, i.Priority.ToString(), StatusLabels.For(i.Mode, i.Status), i.Status, i.LengthMs,
            i.ClientReference, i.Description, i.AssignedUserId is { } a ? users.GetValueOrDefault(a)?.Name : null,
            users.GetValueOrDefault(i.OwnerId)?.Name, i.CreatedAt,
            i.DueDate, overdue, tags, ActionsFor(actor, i, superseded), i.VersionNo, i.FailureReason, IsOutstanding(actor, i, superseded),
            i.Status == ItemStatus.Purged ? 0 : files.GetValueOrDefault(i.ChainId));
    }

    /// <summary>
    /// Speech mode's Outstanding Tasks: what needs this person's attention now. Their own items that are unfinished, with
    /// them (Speech to Text) or failed, and items assigned to them that they have not finished. Never Completed or Deleted.
    /// </summary>
    private static bool IsOutstanding(User u, Item i, bool superseded)
    {
        if (superseded || i.Status is ItemStatus.Completed or ItemStatus.Purged) return false;
        if (i.AssignedUserId == u.Id && i.Status is ItemStatus.AwaitingAssignee or ItemStatus.WithAssignee) return true;
        return i.OwnerId == u.Id && i.Status is ItemStatus.Draft or ItemStatus.WithAuthor or ItemStatus.WithImporter or ItemStatus.ConversionFailed;
    }

    private static IReadOnlyList<string> ActionsFor(User u, Item i, bool superseded)
    {
        var a = new List<string> { "open" };
        var role = PermissionEvaluator.RoleOn(u, i);
        var ownerOrAdmin = role == ItemRole.Owner || u.IsAdmin;
        var idle = i.Processing == ProcessingState.Idle;
        if (i.Status == ItemStatus.ConversionFailed && ownerOrAdmin) a.Add("retry");
        if (i.Status == ItemStatus.Draft && role == ItemRole.Owner) a.Add("continue"); // an unfinished dictation: only its author carries on
        if (i.IsDictation && i.Status == ItemStatus.WithAuthor && idle && !superseded && role == ItemRole.Owner) a.Add("change-recording");
        if (idle && ownerOrAdmin && i.Status is not (ItemStatus.ConversionFailed or ItemStatus.Completed or ItemStatus.Draft)) a.Add("rerun");
        if (idle && i.Status == i.Home && PermissionEvaluator.Evaluate(u, i, Capability.Approve).Allowed) a.Add("approve");
        if (idle && i.Status is ItemStatus.WithAuthor or ItemStatus.WithImporter or ItemStatus.AwaitingAssignee or ItemStatus.WithAssignee
            && PermissionEvaluator.Evaluate(u, i, Capability.Reassign).Allowed) a.Add(i.AssignedUserId is null ? "assign" : "reassign");
        if (i.Status == ItemStatus.AwaitingAssignee && i.AssignedUserId == u.Id) a.Add("accept");
        if (i.Status == ItemStatus.WithAssignee && i.AssignedUserId == u.Id) a.Add("return");
        if (i.Status == ItemStatus.Completed && !superseded && ownerOrAdmin) a.Add("request-reopen");
        if (idle && !superseded && ownerOrAdmin && i.Status is not (ItemStatus.Completed or ItemStatus.Purged))
            a.Add(i.VersionNo > 1 ? "request-delete" : "delete");
        return a;
    }

    private async Task<StagedFile> StageAndScanAsync(User actor, Stream content, string fileName, string kind, CancellationToken ct, long? maxBytes = null)
    {
        StagedFile staged;
        try { staged = await stores.Media.StageAsync(content, maxBytes ?? policy.MaxUploadBytes, ct); }
        catch (Domain_SizeException ex)
        {
            await Audit(actor, "user", null, "upload.rejected", ("kind", kind), ("fileName", SafeName(fileName)), ("reason", "too large"));
            throw new DomainException(ex.Message);
        }
        await using (var s = stores.Media.OpenStaged(staged))
        {
            var scan = await scanner.ScanAsync(s, ct);
            if (scan.Outcome != ScanOutcome.Clean)
            {
                stores.Media.Discard(staged);
                await Audit(actor, "user", null, "upload.rejected", ("kind", kind), ("fileName", SafeName(fileName)),
                    ("scan", scan.Outcome), ("detail", scan.Detail));
                throw new DomainException(scan.Outcome == ScanOutcome.Rejected
                    ? "The file was rejected by the virus scan and has not been stored."
                    : $"The file could not be scanned and has not been stored. {scan.Detail}");
            }
        }
        return staged;
    }

    private static void RequireOwnerOrAdmin(User actor, Item item)
    {
        if (PermissionEvaluator.RoleOn(actor, item) != ItemRole.Owner && !actor.IsAdmin)
            throw new ForbiddenException("Only the author, importer or an administrator can do this.");
    }

    internal static string CapacityOf(User actor, Item item) => PermissionEvaluator.RoleOn(actor, item) switch
    {
        ItemRole.Owner => item.Source == SourceKind.Imported ? "importer" : "author",
        ItemRole.Assignee => "assignee",
        ItemRole.Attendee => "attendee",
        _ => actor.IsAdmin ? "admin" : "user"
    };

    private static string SafeName(string fileName) => Path.GetFileName(fileName).Replace('\\', '_');

    private Task Audit(User actor, string capacity, Guid? itemId, string type, params (string, object?)[] details) =>
        stores.Audit.AppendAsync(actor.Id, capacity, Product, itemId, type, AuditChain.Details(details));
}
