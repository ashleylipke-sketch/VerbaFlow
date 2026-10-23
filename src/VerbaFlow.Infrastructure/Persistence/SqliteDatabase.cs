using Microsoft.Data.Sqlite;

namespace VerbaFlow.Infrastructure.Persistence;

/// <summary>SQLite, for running VerbaFlow on one computer. See <see cref="Database"/> for the rules it enforces.</summary>
public sealed class SqliteDatabase : Database
{
    private readonly string _connectionString;

    public SqliteDatabase(string path)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (dir is not null) Directory.CreateDirectory(dir);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString();
        Initialise();
    }

    public override string InsertOrder => "rowid";
    public override string Describe => "SQLite file on this computer";

    public override SqliteConnection Open()
    {
        var c = new SqliteConnection(_connectionString);
        c.Open();
        using var pragma = c.CreateCommand();
        pragma.CommandText = "PRAGMA busy_timeout=5000;";
        pragma.ExecuteNonQuery();
        return c;
    }

    private void Initialise()
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS doc_users(id TEXT PRIMARY KEY, json TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS doc_items(id TEXT PRIMARY KEY, json TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS doc_transcripts(id TEXT PRIMARY KEY, json TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS doc_outputs(id TEXT PRIMARY KEY, json TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS doc_media(id TEXT PRIMARY KEY, json TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS doc_reopen(id TEXT PRIMARY KEY, json TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS doc_delete_requests(id TEXT PRIMARY KEY, json TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS doc_exports(id TEXT PRIMARY KEY, json TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS doc_vocabulary(id TEXT PRIMARY KEY, json TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS doc_saved_text(id TEXT PRIMARY KEY, json TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS doc_letterhead(id TEXT PRIMARY KEY, json TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS doc_support_errors(id TEXT PRIMARY KEY, json TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS doc_live_text(id TEXT PRIMARY KEY, json TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS doc_widget_sessions(id TEXT PRIMARY KEY, json TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS doc_widget_policy(id TEXT PRIMARY KEY, json TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS audit_events(
                seq INTEGER PRIMARY KEY, at TEXT NOT NULL, actor_id TEXT, capacity TEXT NOT NULL, product TEXT NOT NULL,
                item_id TEXT, type TEXT NOT NULL, details TEXT NOT NULL, prev_hash TEXT NOT NULL, hash TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS ix_audit_item ON audit_events(item_id);

            CREATE TRIGGER IF NOT EXISTS audit_no_update BEFORE UPDATE ON audit_events
              BEGIN SELECT RAISE(ABORT, 'audit events are append-only'); END;
            CREATE TRIGGER IF NOT EXISTS audit_no_delete BEFORE DELETE ON audit_events
              BEGIN SELECT RAISE(ABORT, 'audit events are append-only'); END;

            CREATE TRIGGER IF NOT EXISTS media_no_update BEFORE UPDATE ON doc_media
              BEGIN SELECT RAISE(ABORT, 'media records are write-once'); END;
            CREATE TRIGGER IF NOT EXISTS media_no_delete BEFORE DELETE ON doc_media
              BEGIN SELECT RAISE(ABORT, 'media records are write-once'); END;

            -- An approved item can change in one way only: to deleted (two administrators agreed). A deleted item never changes.
            DROP TRIGGER IF EXISTS item_locked_no_update;
            CREATE TRIGGER item_locked_no_update BEFORE UPDATE ON doc_items
              WHEN json_extract(OLD.json, '$.status') = 'Purged'
                OR (json_extract(OLD.json, '$.status') = 'Completed' AND json_extract(NEW.json, '$.status') IS NOT 'Purged')
              BEGIN SELECT RAISE(ABORT, 'approved items are locked'); END;
            CREATE TRIGGER IF NOT EXISTS item_locked_no_delete BEFORE DELETE ON doc_items
              WHEN json_extract(OLD.json, '$.status') IN ('Completed', 'Purged')
              BEGIN SELECT RAISE(ABORT, 'approved items are locked'); END;

            -- The fingerprint of every exported file is kept as it was recorded: never changed, never removed.
            CREATE TRIGGER IF NOT EXISTS export_no_update BEFORE UPDATE ON doc_exports
              BEGIN SELECT RAISE(ABORT, 'export records are append-only'); END;
            CREATE TRIGGER IF NOT EXISTS export_no_delete BEFORE DELETE ON doc_exports
              BEGIN SELECT RAISE(ABORT, 'export records are append-only'); END;
            """;
        cmd.ExecuteNonQuery();
    }
}
