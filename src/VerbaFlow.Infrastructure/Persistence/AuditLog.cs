using VerbaFlow.Core.Audit;

namespace VerbaFlow.Infrastructure.Persistence;

public sealed class AuditLog(Database db, TimeProvider clock)
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<AuditEvent> AppendAsync(Guid? actor, string capacity, string product, Guid? itemId, string type, string details)
    {
        await _gate.WaitAsync();
        try
        {
            await using var c = db.Open();
            await using var tx = await c.BeginTransactionAsync();
            if (db is PostgresDatabase)
            {
                await using var lk = c.CreateCommand();
                lk.Transaction = tx;
                lk.CommandText = PostgresDatabase.AuditLock;
                await lk.ExecuteNonQueryAsync();
            }
            long seq = 1; var prev = AuditChain.Genesis;
            await using (var last = c.CreateCommand())
            {
                last.Transaction = tx;
                last.CommandText = "SELECT seq, hash FROM audit_events ORDER BY seq DESC LIMIT 1";
                await using var r = await last.ExecuteReaderAsync();
                if (await r.ReadAsync()) { seq = r.GetInt64(0) + 1; prev = r.GetString(1); }
            }
            var e = AuditChain.Build(seq, clock.GetUtcNow(), actor, capacity, product, itemId, type, details, prev);
            await using (var ins = c.CreateCommand())
            {
                ins.Transaction = tx;
                ins.CommandText = """
                    INSERT INTO audit_events(seq, at, actor_id, capacity, product, item_id, type, details, prev_hash, hash)
                    VALUES(@seq,@at,@actor,@cap,@prod,@item,@type,@details,@prev,@hash)
                    """;
                ins.Add("@seq", e.Seq);
                ins.Add("@at", e.At.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ"));
                ins.Add("@actor", e.ActorId?.ToString());
                ins.Add("@cap", e.Capacity);
                ins.Add("@prod", e.Product);
                ins.Add("@item", e.ItemId?.ToString());
                ins.Add("@type", e.Type);
                ins.Add("@details", e.Details);
                ins.Add("@prev", e.PrevHash);
                ins.Add("@hash", e.Hash);
                await ins.ExecuteNonQueryAsync();
            }
            await tx.CommitAsync();
            return e;
        }
        finally { _gate.Release(); }
    }

    public async Task<IReadOnlyList<AuditEvent>> ListAsync(Guid? itemId = null)
    {
        await using var c = db.Open();
        await using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT seq, at, actor_id, capacity, product, item_id, type, details, prev_hash, hash FROM audit_events"
            + (itemId is null ? "" : " WHERE item_id = @item") + " ORDER BY seq";
        if (itemId is not null) cmd.Add("@item", itemId.ToString());
        var list = new List<AuditEvent>();
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
            list.Add(new AuditEvent(r.GetInt64(0),
                DateTimeOffset.Parse(r.GetString(1), null, System.Globalization.DateTimeStyles.RoundtripKind),
                r.IsDBNull(2) ? null : Guid.Parse(r.GetString(2)), r.GetString(3), r.GetString(4),
                r.IsDBNull(5) ? null : Guid.Parse(r.GetString(5)), r.GetString(6), r.GetString(7), r.GetString(8), r.GetString(9)));
        return list;
    }

    public async Task<AuditVerification> VerifyAsync() => AuditChain.Verify(await ListAsync());
}
