using System.Security.Cryptography;
using VerbaFlow.Infrastructure.Persistence;

namespace VerbaFlow.Infrastructure.Media;

public sealed record MediaAsset(Guid Id, Guid ItemId, string Kind, string Sha256, long SizeBytes, string FileName,
    string ContentType, string Provenance, DateTimeOffset StoredAt);

/// <summary>An upload held in quarantine until it has been scanned. Not part of the record yet.</summary>
public sealed record StagedFile(string Path, long SizeBytes, string Sha256);

/// <summary>
/// Original recordings are write-once: hashed as they arrive, stored read-only, and never modified.
/// Production equivalent: Blob Storage with an immutability policy.
/// </summary>
public sealed class FileMediaStore(string root, DocTable<MediaAsset> table, TimeProvider clock)
{
    public async Task<StagedFile> StageAsync(Stream content, long maxBytes, CancellationToken ct)
    {
        var dir = Path.Combine(root, ".quarantine");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, Guid.NewGuid().ToString("N"));
        using var sha = SHA256.Create();
        long total = 0;
        try
        {
            await using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            var buffer = new byte[81920];
            int n;
            while ((n = await content.ReadAsync(buffer, ct)) > 0)
            {
                total += n;
                if (total > maxBytes) throw new Domain_SizeException(maxBytes);
                sha.TransformBlock(buffer, 0, n, null, 0);
                await file.WriteAsync(buffer.AsMemory(0, n), ct);
            }
            sha.TransformFinalBlock([], 0, 0);
        }
        catch { TryDelete(path); throw; }
        return new StagedFile(path, total, Convert.ToHexString(sha.Hash!).ToLowerInvariant());
    }

    public Stream OpenStaged(StagedFile f) => new FileStream(f.Path, FileMode.Open, FileAccess.Read, FileShare.Read);

    public void Discard(StagedFile f) => TryDelete(f.Path);

    public async Task<MediaAsset> CommitOriginalAsync(StagedFile staged, Guid itemId, string fileName, string contentType, string provenance)
    {
        var id = Guid.NewGuid();
        var dir = Path.Combine(root, itemId.ToString("N"));
        Directory.CreateDirectory(dir);
        var dest = Path.Combine(dir, id.ToString("N") + ".bin");
        File.Move(staged.Path, dest);
        File.SetAttributes(dest, FileAttributes.ReadOnly);
        var asset = new MediaAsset(id, itemId, "original", staged.Sha256, staged.SizeBytes, fileName, contentType, provenance, clock.GetUtcNow());
        await table.UpsertAsync(id, asset);
        return asset;
    }

    public async Task<MediaAsset?> FindOriginalAsync(Guid itemId) =>
        (await table.ListAsync()).FirstOrDefault(a => a.ItemId == itemId && a.Kind == "original");

    public Stream OpenRead(MediaAsset a) =>
        new FileStream(PathFor(a), FileMode.Open, FileAccess.Read, FileShare.Read);

    /// <summary>Recomputes the hash of the stored file and compares it with the hash recorded at capture.</summary>
    public async Task<bool> VerifyAsync(MediaAsset a)
    {
        await using var s = OpenRead(a);
        var h = Convert.ToHexString(await SHA256.HashDataAsync(s)).ToLowerInvariant();
        return h == a.Sha256;
    }

    /// <summary>
    /// Deletes the stored audio files of an item when the recording is deleted. The media records (hash, size, when stored)
    /// stay as the tombstone; only the content goes.
    /// </summary>
    public async Task<int> DeleteContentAsync(Guid itemId)
    {
        var removed = 0;
        foreach (var a in (await table.ListAsync()).Where(a => a.ItemId == itemId))
        {
            var path = PathFor(a);
            if (!File.Exists(path)) continue;
            File.SetAttributes(path, FileAttributes.Normal);
            File.Delete(path);
            removed++;
        }
        var dir = Path.Combine(root, itemId.ToString("N"));
        if (Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir);
        return removed;
    }

    private string PathFor(MediaAsset a) => Path.Combine(root, a.ItemId.ToString("N"), a.Id.ToString("N") + ".bin");

    private static void TryDelete(string path) { try { if (File.Exists(path)) File.Delete(path); } catch { /* best effort */ } }
}

public sealed class Domain_SizeException(long maxBytes)
    : Exception($"The file is larger than the company limit of {maxBytes / (1024 * 1024)} MB.");
