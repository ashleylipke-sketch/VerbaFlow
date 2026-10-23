using VerbaFlow.Core.Domain;
using VerbaFlow.Core.Providers;
using VerbaFlow.Core.Transcripts;
using VerbaFlow.Infrastructure.Media;
using VerbaFlow.Infrastructure.Persistence;

namespace VerbaFlow.Infrastructure.Services;

public sealed record StoredOutputs(Guid ItemId, AiOutputs Outputs);

/// <summary>All the stores the application services use, built once.</summary>
public sealed class Stores
{
    public DocTable<User> Users { get; }
    public DocTable<Item> Items { get; }
    public DocTable<Transcript> Transcripts { get; }
    public DocTable<StoredOutputs> Outputs { get; }
    public DocTable<ReopenRequest> Reopen { get; }
    public DocTable<DeleteRequest> Deletes { get; }
    public DocTable<MediaAsset> MediaAssets { get; }
    public DocTable<VocabularyTerm> Vocabulary { get; }
    public DocTable<SavedText> SavedText { get; }
    public DocTable<Letterhead> Letterhead { get; }
    /// <summary>Uploaded letterhead files, each kept under its fingerprint.</summary>
    public string LetterheadRoot { get; }
    public DocTable<SupportError> SupportErrors { get; }
    public DocTable<Export.ExportRecord> Exports { get; }
    /// <summary>Speech mode: the text heard live while the person spoke, kept with the item it was saved as.</summary>
    public DocTable<LiveTranscript> LiveText { get; }
    /// <summary>Widget: facts about each use (site, times, outcome), never text or audio.</summary>
    public DocTable<WidgetSession> WidgetSessions { get; }
    public DocTable<WidgetPolicy> WidgetPolicy { get; }
    public AuditLog Audit { get; }
    public FileMediaStore Media { get; }

    public Stores(Database db, string mediaRoot, TimeProvider clock)
    {
        Users = new(db, "doc_users");
        Items = new(db, "doc_items");
        Transcripts = new(db, "doc_transcripts");
        Outputs = new(db, "doc_outputs");
        Reopen = new(db, "doc_reopen");
        Deletes = new(db, "doc_delete_requests");
        MediaAssets = new(db, "doc_media");
        Vocabulary = new(db, "doc_vocabulary");
        SavedText = new(db, "doc_saved_text");
        Letterhead = new(db, "doc_letterhead");
        LetterheadRoot = Path.Combine(mediaRoot, "letterhead");
        SupportErrors = new(db, "doc_support_errors");
        Exports = new(db, "doc_exports");
        LiveText = new(db, "doc_live_text");
        WidgetSessions = new(db, "doc_widget_sessions");
        WidgetPolicy = new(db, "doc_widget_policy");
        Audit = new(db, clock);
        Media = new(mediaRoot, MediaAssets, clock);
    }

    public async Task<Item> RequireItemAsync(Guid id) =>
        await Items.GetAsync(id) ?? throw new NotFoundException("That item does not exist.");

    public async Task<User> RequireUserAsync(Guid id) =>
        await Users.GetAsync(id) ?? throw new NotFoundException("That user does not exist.");
}
