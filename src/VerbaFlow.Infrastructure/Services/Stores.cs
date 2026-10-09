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
    public DocTable<MediaAsset> MediaAssets { get; }
    public DocTable<VocabularyTerm> Vocabulary { get; }
    public DocTable<SupportError> SupportErrors { get; }
    public AuditLog Audit { get; }
    public FileMediaStore Media { get; }

    public Stores(SqliteDatabase db, string mediaRoot, TimeProvider clock)
    {
        Users = new(db, "doc_users");
        Items = new(db, "doc_items");
        Transcripts = new(db, "doc_transcripts");
        Outputs = new(db, "doc_outputs");
        Reopen = new(db, "doc_reopen");
        MediaAssets = new(db, "doc_media");
        Vocabulary = new(db, "doc_vocabulary");
        SupportErrors = new(db, "doc_support_errors");
        Audit = new(db, clock);
        Media = new(mediaRoot, MediaAssets, clock);
    }

    public async Task<Item> RequireItemAsync(Guid id) =>
        await Items.GetAsync(id) ?? throw new NotFoundException("That item does not exist.");

    public async Task<User> RequireUserAsync(Guid id) =>
        await Users.GetAsync(id) ?? throw new NotFoundException("That user does not exist.");
}
