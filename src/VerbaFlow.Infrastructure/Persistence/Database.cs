using System.Data.Common;

namespace VerbaFlow.Infrastructure.Persistence;

/// <summary>
/// Where VerbaFlow keeps its records: SQLite on one computer, PostgreSQL online. Both enforce the same rules in the
/// database itself: audit events, media records and export fingerprints cannot be changed or removed, and an approved
/// item can change only to deleted.
/// </summary>
public abstract class Database
{
    /// <summary>The document tables: one JSON document per id.</summary>
    public static readonly string[] DocTables =
    [
        "doc_users", "doc_items", "doc_transcripts", "doc_outputs", "doc_media", "doc_reopen", "doc_delete_requests",
        "doc_exports", "doc_vocabulary", "doc_saved_text", "doc_letterhead", "doc_support_errors", "doc_live_text",
        "doc_widget_sessions", "doc_widget_policy", "doc_note_takers", "doc_meeting_speakers",
    ];

    /// <summary>An open connection.</summary>
    public abstract DbConnection Open();

    /// <summary>The column that keeps documents in the order they were first saved.</summary>
    public abstract string InsertOrder { get; }

    /// <summary>For the startup log: which database, never the password.</summary>
    public abstract string Describe { get; }
}

internal static class DbCommandExtensions
{
    public static void Add(this DbCommand cmd, string name, object? value)
    {
        var p = cmd.CreateParameter();
        p.ParameterName = name;
        p.Value = value ?? DBNull.Value;
        cmd.Parameters.Add(p);
    }
}
