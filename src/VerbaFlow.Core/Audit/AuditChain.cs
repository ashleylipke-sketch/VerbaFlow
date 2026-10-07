using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace VerbaFlow.Core.Audit;

public sealed record AuditEvent(
    long Seq,
    DateTimeOffset At,
    Guid? ActorId,
    string Capacity,
    string Product,
    Guid? ItemId,
    string Type,
    string Details,
    string PrevHash,
    string Hash);

public sealed record AuditVerification(bool Ok, int Count, long? BadSeq, string? Problem);

/// <summary>
/// The audit trail is append-only and hash-chained: each entry's hash covers its content and the
/// previous entry's hash, so editing or deleting any entry breaks every later hash.
/// </summary>
public static class AuditChain
{
    public static readonly string Genesis = new('0', 64);

    public static string Canonical(long seq, DateTimeOffset at, Guid? actor, string capacity, string product,
        Guid? itemId, string type, string details, string prevHash) =>
        string.Join('|', seq, at.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ"),
            actor?.ToString() ?? "-", capacity, product, itemId?.ToString() ?? "-", type, details, prevHash);

    public static string ComputeHash(string canonical) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();

    public static AuditEvent Build(long seq, DateTimeOffset at, Guid? actor, string capacity, string product,
        Guid? itemId, string type, string details, string prevHash) =>
        new(seq, at, actor, capacity, product, itemId, type, details, prevHash,
            ComputeHash(Canonical(seq, at, actor, capacity, product, itemId, type, details, prevHash)));

    public static AuditVerification Verify(IReadOnlyList<AuditEvent> events)
    {
        var prev = Genesis;
        long expectedSeq = 1;
        foreach (var e in events)
        {
            if (e.Seq != expectedSeq)
                return new(false, events.Count, e.Seq, $"Entry {expectedSeq} is missing (found {e.Seq}).");
            if (e.PrevHash != prev)
                return new(false, events.Count, e.Seq, "The link to the previous entry is broken.");
            var recomputed = ComputeHash(Canonical(e.Seq, e.At, e.ActorId, e.Capacity, e.Product, e.ItemId, e.Type, e.Details, e.PrevHash));
            if (recomputed != e.Hash)
                return new(false, events.Count, e.Seq, "The entry's content does not match its hash.");
            prev = e.Hash;
            expectedSeq++;
        }
        return new(true, events.Count, null, null);
    }

    /// <summary>Stable JSON with sorted keys, so the same details always hash the same.</summary>
    public static string Details(params (string Key, object? Value)[] pairs)
    {
        var sorted = new SortedDictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (k, v) in pairs) sorted[k] = v is Enum ? v.ToString() : v;
        return JsonSerializer.Serialize(sorted);
    }
}
