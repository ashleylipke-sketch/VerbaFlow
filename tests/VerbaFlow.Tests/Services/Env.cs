using System.Text;
using Microsoft.Data.Sqlite;
using VerbaFlow.Core.Domain;
using VerbaFlow.Core.Providers;
using VerbaFlow.Infrastructure.Persistence;
using VerbaFlow.Infrastructure.Services;
using VerbaFlow.Infrastructure.StandIns;

namespace VerbaFlow.Tests.Services;

public sealed class FixedClock : TimeProvider
{
    public DateTimeOffset Now { get; set; } = T.Now;
    public override DateTimeOffset GetUtcNow() => Now;
}

public sealed class ThrowingSpeech : ISpeechService
{
    public bool Fail { get; set; } = true;
    public Task<VerbaFlow.Core.Providers.TranscriptionResult> TranscribeAsync(Stream audio, TranscribeOptions options, CancellationToken ct) =>
        Fail ? throw new InvalidOperationException("speech service unavailable") : new StandInSpeechService().TranscribeAsync(audio, options, ct);
}

/// <summary>A fresh database, media folder and set of services per test.</summary>
public sealed class Env : IDisposable
{
    public string Dir { get; } = Directory.CreateTempSubdirectory("verbaflow-test-").FullName;
    public FixedClock Clock { get; } = new();
    public SqliteDatabase Db { get; }
    public Stores Stores { get; }
    public ThrowingSpeech Speech { get; } = new() { Fail = false };
    public ProcessingQueue Queue { get; } = new();
    public MeetingService Meeting { get; }
    public ProcessingService Processing { get; }
    public User Alice { get; } = T.U("Alice");
    public User Bob { get; } = T.U("Bob");
    public User Carol { get; } = T.U("Carol", admin: true);
    public User Dave { get; } = T.U("Dave", admin: true);
    public User Xavier { get; } = T.U("Xavier");
    public string DbPath => Path.Combine(Dir, "test.db");

    public Env(PlatformPolicy? policy = null)
    {
        Db = new SqliteDatabase(DbPath);
        Stores = new Stores(Db, Path.Combine(Dir, "media"), Clock);
        Meeting = new MeetingService(Stores, new StandInMalwareScanner(), Queue, policy ?? new PlatformPolicy(), Clock);
        Processing = new ProcessingService(Stores, Speech, new StandInAiOutputService(), new StandInAudioEnhancer(), Clock);
        foreach (var u in new[] { Alice, Bob, Carol, Dave, Xavier }) Stores.Users.UpsertAsync(u.Id, u).GetAwaiter().GetResult();
    }

    public static Stream Audio(string content = "pretend this is audio") => new MemoryStream(Encoding.UTF8.GetBytes(content));

    public RecordingUpload Recording(string name = "Board meeting", bool consent = true, string audio = "pretend this is audio",
        IReadOnlyList<MarkerInput>? markers = null) =>
        new(Audio(audio), "meeting.webm", "audio/webm", name, 60_000, "en", consent, markers ?? []);

    public ImportUpload Import(string fileName = "call.mp4", bool rights = true, string content = "pretend this is video") =>
        new(Audio(content), fileName, "video/mp4", null, 0, "en", rights, "supplied by client");

    public async Task<Guid> ProcessedRecordingAsync(User? by = null)
    {
        var id = await Meeting.CreateRecordingAsync(by ?? Alice, Recording());
        await Processing.ProcessAsync(id);
        return id;
    }

    public async Task<Guid> ProcessedImportAsync(User? by = null)
    {
        var id = await Meeting.ImportAsync(by ?? Alice, Import());
        await Processing.ProcessAsync(id);
        return id;
    }

    public async Task<Item> Item(Guid id) => await Stores.Items.GetAsync(id) ?? throw new Exception("missing");

    public void Execute(string sql)
    {
        using var c = Db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(Dir, true); } catch { /* read-only media files on some platforms */ }
    }
}
