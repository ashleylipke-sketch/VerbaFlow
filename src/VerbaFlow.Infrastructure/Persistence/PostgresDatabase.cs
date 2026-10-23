using System.Data.Common;
using Npgsql;

namespace VerbaFlow.Infrastructure.Persistence;

/// <summary>
/// PostgreSQL, for VerbaFlow online (Azure Database for PostgreSQL). Same tables and the same rules as SQLite,
/// enforced by triggers: see <see cref="Database"/>.
/// </summary>
public sealed class PostgresDatabase : Database, IDisposable
{
    private readonly NpgsqlDataSource _source;
    private readonly string _describe;

    public PostgresDatabase(string connectionString)
    {
        var b = new NpgsqlConnectionStringBuilder(connectionString);
        // The smallest Azure server allows about 50 connections; VerbaFlow needs far fewer.
        if (!connectionString.Contains("Maximum Pool Size", StringComparison.OrdinalIgnoreCase)) b.MaxPoolSize = 20;
        _describe = $"PostgreSQL on {b.Host}, database {b.Database}";
        _source = NpgsqlDataSource.Create(b.ConnectionString);
        Initialise();
    }

    public override string InsertOrder => "ord";
    public override string Describe => _describe;
    public override DbConnection Open() => _source.OpenConnection();
    public void Dispose() => _source.Dispose();

    /// <summary>Taken inside the audit transaction, so two copies of VerbaFlow (for a moment during an update) never write the same entry number.</summary>
    public const string AuditLock = "LOCK TABLE audit_events IN SHARE ROW EXCLUSIVE MODE";

    private void Initialise()
    {
        using var c = _source.OpenConnection();
        using var cmd = c.CreateCommand();
        var tables = string.Join("\n", DocTables.Select(t =>
            $"CREATE TABLE IF NOT EXISTS {t}(id TEXT PRIMARY KEY, json TEXT NOT NULL, ord BIGINT GENERATED ALWAYS AS IDENTITY);"));
        cmd.CommandText = $$"""
            -- Two copies starting at once must not create the same things twice.
            SELECT pg_advisory_xact_lock(4711);
            {{tables}}
            CREATE TABLE IF NOT EXISTS audit_events(
                seq BIGINT PRIMARY KEY, at TEXT NOT NULL, actor_id TEXT, capacity TEXT NOT NULL, product TEXT NOT NULL,
                item_id TEXT, type TEXT NOT NULL, details TEXT NOT NULL, prev_hash TEXT NOT NULL, hash TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS ix_audit_item ON audit_events(item_id);

            CREATE OR REPLACE FUNCTION vf_refuse() RETURNS trigger LANGUAGE plpgsql AS $f$
            BEGIN RAISE EXCEPTION '%', TG_ARGV[0]; END $f$;

            CREATE OR REPLACE TRIGGER audit_no_change BEFORE UPDATE OR DELETE ON audit_events
              FOR EACH ROW EXECUTE FUNCTION vf_refuse('audit events are append-only');
            CREATE OR REPLACE TRIGGER audit_no_truncate BEFORE TRUNCATE ON audit_events
              FOR EACH STATEMENT EXECUTE FUNCTION vf_refuse('audit events are append-only');

            CREATE OR REPLACE TRIGGER media_no_change BEFORE UPDATE OR DELETE ON doc_media
              FOR EACH ROW EXECUTE FUNCTION vf_refuse('media records are write-once');
            CREATE OR REPLACE TRIGGER media_no_truncate BEFORE TRUNCATE ON doc_media
              FOR EACH STATEMENT EXECUTE FUNCTION vf_refuse('media records are write-once');

            -- The fingerprint of every exported file is kept as it was recorded: never changed, never removed.
            CREATE OR REPLACE TRIGGER export_no_change BEFORE UPDATE OR DELETE ON doc_exports
              FOR EACH ROW EXECUTE FUNCTION vf_refuse('export records are append-only');
            CREATE OR REPLACE TRIGGER export_no_truncate BEFORE TRUNCATE ON doc_exports
              FOR EACH STATEMENT EXECUTE FUNCTION vf_refuse('export records are append-only');

            -- An approved item can change in one way only: to deleted (two administrators agreed). A deleted item never changes.
            CREATE OR REPLACE FUNCTION vf_item_lock() RETURNS trigger LANGUAGE plpgsql AS $f$
            DECLARE was text := OLD.json::jsonb ->> 'status';
            BEGIN
              IF TG_OP = 'DELETE' THEN
                IF was IN ('Completed', 'Purged') THEN RAISE EXCEPTION 'approved items are locked'; END IF;
                RETURN OLD;
              END IF;
              IF was = 'Purged' OR (was = 'Completed' AND (NEW.json::jsonb ->> 'status') IS DISTINCT FROM 'Purged') THEN
                RAISE EXCEPTION 'approved items are locked';
              END IF;
              RETURN NEW;
            END $f$;
            CREATE OR REPLACE TRIGGER item_locked BEFORE UPDATE OR DELETE ON doc_items
              FOR EACH ROW EXECUTE FUNCTION vf_item_lock();
            CREATE OR REPLACE TRIGGER item_no_truncate BEFORE TRUNCATE ON doc_items
              FOR EACH STATEMENT EXECUTE FUNCTION vf_refuse('approved items are locked');
            """;
        using var tx = c.BeginTransaction();
        cmd.Transaction = tx;
        cmd.ExecuteNonQuery();
        tx.Commit();
    }
}
