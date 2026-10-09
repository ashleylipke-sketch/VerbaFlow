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
        if (!up.ConsentNoticeGiven)
            throw new DomainException("Confirm that everyone was told the meeting is being recorded before saving.");
        if (up.LengthMs <= 0) throw new DomainException("The recording has no length.");

        var staged = await StageAndScanAsync(actor, up.Audio, up.FileName, "recording", ct);
        var id = Guid.NewGuid();
        var now = clock.GetUtcNow();
        var item = Item.CreateRecorded(id, actor.Id, up.Name, up.LengthMs, up.OutputLanguage, now);
        item.AddMarker(new TimelineMarker { Type = MarkerType.ConsentNotice, OffsetMs = 0, Note = "Notice given before recording", ActorId = actor.Id, At = now });
        foreach (var m in up.Markers.OrderBy(m => m.OffsetMs))
            item.AddMarker(new TimelineMarker { Type = m.Type, OffsetMs = m.OffsetMs, Note = m.Note, ActorId = actor.Id, At = now });

        var asset = await stores.Media.CommitOriginalAsync(staged, id, SafeName(up.FileName), up.ContentType, "Recorded in app");
        await stores.Items.UpsertAsync(id, item);
        await Audit(actor, "author", id, "item.created", ("source", "Recorded in app"), ("sha256", asset.Sha256),
            ("sizeBytes", asset.SizeBytes), ("lengthMs", up.LengthMs), ("consentNoticeConfirmed", true));
        foreach (var m in item.Markers.Where(m => m.Type is MarkerType.Objection or MarkerType.Pause))
            await Audit(actor, "author", id, m.Type == MarkerType.Objection ? "recording.objection_logged" : "recording.paused",
                ("offsetMs", m.OffsetMs), ("note", m.Note));
        queue.Enqueue(id);
        return id;
    }

    public async Task<Guid> ImportAsync(User actor, ImportUpload up, CancellationToken ct = default)
    {
        if (!up.RightsConfirmed)
            throw new DomainException("Confirm that you have the right to process this file before importing.");
        var ext = Path.GetExtension(up.FileName);
        if (!MediaExtensions.Contains(ext) && !(up.ContentType.StartsWith("audio/") || up.ContentType.StartsWith("video/")))
            throw new DomainException("Only audio and video files can be imported. Attach documents to an existing item instead.");

        var staged = await StageAndScanAsync(actor, up.File, up.FileName, "import", ct);
        var id = Guid.NewGuid();
        var name = string.IsNullOrWhiteSpace(up.Name) ? Path.GetFileNameWithoutExtension(up.FileName) : up.Name!;
        var item = Item.CreateImported(id, actor.Id, name, up.LengthMs, up.OutputLanguage, clock.GetUtcNow(), SafeName(up.FileName), up.SourceNote);
        var asset = await stores.Media.CommitOriginalAsync(staged, id, SafeName(up.FileName), up.ContentType, "Imported");
        await stores.Items.UpsertAsync(id, item);
        await Audit(actor, "importer", id, "item.imported", ("source", "Imported (external)"), ("originalFileName", SafeName(up.FileName)),
            ("claimedContentType", up.ContentType), ("sha256", asset.Sha256), ("sizeBytes", asset.SizeBytes),
            ("rightsConfirmed", true), ("sourceNote", up.SourceNote));
        queue.Enqueue(id);
        return id;
    }

    public async Task RetryAsync(User actor, Guid id)
    {
        var item = await stores.RequireItemAsync(id);
        RequireOwnerOrAdmin(actor, item);
        item.Retry();
        await stores.Items.UpsertAsync(id, item);
        await Audit(actor, CapacityOf(actor, item), id, "processing.retry_requested");
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
        var asset = await stores.Media.FindOriginalAsync(item.MediaSourceItemId);
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
                r.Segment.Words?.Select(w => new WordView(w.Text, w.StartMs, w.EndMs)).ToList(), OriginalOf(i))).ToList(),
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
        var asset = await stores.Media.FindOriginalAsync(item.MediaSourceItemId) ?? throw new NotFoundException("The recording is missing.");
        await Audit(actor, CapacityOf(actor, item), id, download ? "audio.downloaded" : "audio.played", ("sha256", asset.Sha256));
        return new AudioHandle(stores.Media.OpenRead(asset), asset.ContentType, asset.FileName);
    }

    // ---------- dashboard ----------

    public async Task<IReadOnlyList<ItemRow>> ListAsync(User actor, ListQuery q)
    {
        var all = await stores.Items.ListAsync();
        var users = (await stores.Users.ListAsync()).ToDictionary(u => u.Id);
        var superseded = all.Where(i => i.SupersedesItemId is not null).Select(i => i.SupersedesItemId!.Value).ToHashSet();
        var rows = new List<ItemRow>();
        foreach (var i in all.Where(i => PermissionEvaluator.CanSee(actor, i)))
        {
            if (q.Statuses is not null && !q.Statuses.Contains(i.Status)) continue;
            if (q.SelfAssigned is { } sa && i.IsSelfAssigned != sa) continue;
            if (!string.IsNullOrWhiteSpace(q.Search) &&
                !$"{i.Name} {i.ClientReference} {i.Description}".Contains(q.Search, StringComparison.OrdinalIgnoreCase)) continue;
            rows.Add(ToRow(actor, i, users, superseded.Contains(i.Id)));
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
        var asset = await stores.Media.FindOriginalAsync(item.MediaSourceItemId);
        var chain = all.Where(i => i.ChainId == item.ChainId).OrderBy(i => i.VersionNo).ToList();
        var role = PermissionEvaluator.RoleOn(actor, item);
        var ownerRole = item.Source == SourceKind.Imported ? "Importer" : "Author";
        return new ItemDetail(ToRow(actor, item, users, superseded), users.GetValueOrDefault(item.OwnerId)?.Name ?? "?", ownerRole,
            item.Processing == ProcessingState.Running ? "Processing" : null, item.FailureReason, item.OriginalFileName,
            item.ExternalSourceNote, asset?.Sha256,
            item.Markers.Select(m => new MarkerView(m.Type.ToString(), m.OffsetMs, m.Note, users.GetValueOrDefault(m.ActorId)?.Name ?? "?", m.At)).ToList(),
            PermissionEvaluator.Evaluate(actor, item, Capability.Write).Allowed,
            PermissionEvaluator.Evaluate(actor, item, Capability.Write).Allowed && (role == ItemRole.Owner || actor.IsAdmin),
            PermissionEvaluator.Evaluate(actor, item, Capability.ExportAudio).Allowed,
            actor.IsAdmin && role == ItemRole.None ? "Admin" : role.ToString(), item.OutputLanguage, item.ApprovedAt,
            item.ApprovedBy is { } a ? users.GetValueOrDefault(a)?.Name : null, item.ChainId,
            chain.Select(c => new ChainEntry(c.Id, c.VersionNo, StatusLabels.For(c.Mode, c.Status), !all.Any(x => x.SupersedesItemId == c.Id))).ToList(),
            item.Source == SourceKind.Imported ? "Imported (external source)" : "Recorded in app");
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

    private ItemRow ToRow(User actor, Item i, Dictionary<Guid, User> users, bool superseded)
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
            i.DueDate, overdue, tags, ActionsFor(actor, i, superseded), i.VersionNo);
    }

    private static IReadOnlyList<string> ActionsFor(User u, Item i, bool superseded)
    {
        var a = new List<string> { "open" };
        var role = PermissionEvaluator.RoleOn(u, i);
        var ownerOrAdmin = role == ItemRole.Owner || u.IsAdmin;
        var idle = i.Processing == ProcessingState.Idle;
        if (i.Status == ItemStatus.ConversionFailed && ownerOrAdmin) a.Add("retry");
        if (idle && i.Status == i.Home && PermissionEvaluator.Evaluate(u, i, Capability.Approve).Allowed) a.Add("approve");
        if (idle && i.Status is ItemStatus.WithAuthor or ItemStatus.WithImporter or ItemStatus.AwaitingAssignee or ItemStatus.WithAssignee
            && PermissionEvaluator.Evaluate(u, i, Capability.Reassign).Allowed) a.Add(i.AssignedUserId is null ? "assign" : "reassign");
        if (i.Status == ItemStatus.AwaitingAssignee && i.AssignedUserId == u.Id) a.Add("accept");
        if (i.Status == ItemStatus.WithAssignee && i.AssignedUserId == u.Id) a.Add("return");
        if (i.Status == ItemStatus.Completed && !superseded && ownerOrAdmin) a.Add("request-reopen");
        return a;
    }

    private async Task<StagedFile> StageAndScanAsync(User actor, Stream content, string fileName, string kind, CancellationToken ct)
    {
        StagedFile staged;
        try { staged = await stores.Media.StageAsync(content, policy.MaxUploadBytes, ct); }
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

    private static string CapacityOf(User actor, Item item) => PermissionEvaluator.RoleOn(actor, item) switch
    {
        ItemRole.Owner => item.Source == SourceKind.Imported ? "importer" : "author",
        ItemRole.Assignee => "assignee",
        _ => actor.IsAdmin ? "admin" : "user"
    };

    private static string SafeName(string fileName) => Path.GetFileName(fileName).Replace('\\', '_');

    private Task Audit(User actor, string capacity, Guid? itemId, string type, params (string, object?)[] details) =>
        stores.Audit.AppendAsync(actor.Id, capacity, Product, itemId, type, AuditChain.Details(details));
}
