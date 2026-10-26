using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http.Features;
using VerbaFlow.Core.Domain;
using VerbaFlow.Infrastructure.Persistence;
using VerbaFlow.Infrastructure.Services;
using VerbaFlow.Infrastructure.StandIns;
using VerbaFlow.Core.Providers;
using VerbaFlow.Api;

var builder = WebApplication.CreateBuilder(args);
var dataDir = builder.Configuration["VerbaFlow:DataDir"] ?? Path.Combine(builder.Environment.ContentRootPath, "data");
Directory.CreateDirectory(dataDir);
var policy = new PlatformPolicy(builder.Configuration.GetValue("VerbaFlow:MaxUploadBytes", 500L * 1024 * 1024),
    builder.Configuration.GetValue("VerbaFlow:BlockSelfApproval", false));

builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
    o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
});
builder.Services.Configure<FormOptions>(o => o.MultipartBodyLengthLimit = policy.MaxUploadBytes + 1024 * 1024);
builder.WebHost.ConfigureKestrel(k => k.Limits.MaxRequestBodySize = policy.MaxUploadBytes + 1024 * 1024);

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton(policy);
// Online: PostgreSQL (Database:ConnectionString). On one computer: a SQLite file in the data folder.
var pgConnection = builder.Configuration["Database:ConnectionString"];
Database database = string.IsNullOrWhiteSpace(pgConnection)
    ? new SqliteDatabase(Path.Combine(dataDir, "verbaflow.db"))
    : new PostgresDatabase(pgConnection);
builder.Services.AddSingleton(database);
builder.Services.AddSingleton(sp => new Stores(sp.GetRequiredService<Database>(), Path.Combine(dataDir, "media"), sp.GetRequiredService<TimeProvider>()));
builder.Services.AddSingleton<ProcessingQueue>();
// Stand-ins: swapped for Azure services behind these same interfaces.
var speech = new VerbaFlow.Infrastructure.Azure.AzureSpeechOptions(
    builder.Configuration["Speech:Endpoint"] ?? "", builder.Configuration["Speech:Key"] ?? "");
if (speech.IsConfigured)
{
    builder.Services.AddSingleton(speech);
    builder.Services.AddHttpClient<VerbaFlow.Infrastructure.Azure.AzureSpeechService>(c => c.Timeout = TimeSpan.FromMinutes(30));
}
else builder.Services.AddSingleton<StandInSpeechService>();
var openAi = new VerbaFlow.Infrastructure.Azure.AzureOpenAiOptions(builder.Configuration["OpenAI:Endpoint"] ?? "",
    builder.Configuration["OpenAI:Key"] ?? "", builder.Configuration["OpenAI:Deployment"] ?? "");
if (openAi.IsConfigured)
{
    builder.Services.AddSingleton(openAi);
    builder.Services.AddHttpClient<IAiOutputService, VerbaFlow.Infrastructure.Azure.AzureOpenAiOutputService>(c => c.Timeout = TimeSpan.FromMinutes(10));
}
else builder.Services.AddSingleton<IAiOutputService, StandInAiOutputService>();
if (openAi.IsConfigured) builder.Services.AddHttpClient<IGrammarChecker, VerbaFlow.Infrastructure.Azure.AzureOpenAiGrammarChecker>(c => c.Timeout = TimeSpan.FromMinutes(5));
else builder.Services.AddSingleton<IGrammarChecker, StandInGrammarChecker>();
builder.Services.AddSingleton<GrammarService>();
builder.Services.AddSingleton<WidgetService>();
builder.Services.AddSingleton<IMalwareScanner, StandInMalwareScanner>();
var emailOptions = new VerbaFlow.Infrastructure.Azure.AzureEmailOptions(builder.Configuration["Email:ConnectionString"] ?? "", builder.Configuration["Email:Sender"] ?? "");
var outbox = Path.Combine(dataDir, "outbox");
if (emailOptions.IsConfigured) builder.Services.AddSingleton<IEmailSender>(new VerbaFlow.Infrastructure.Azure.AzureEmailSender(emailOptions));
else builder.Services.AddSingleton<IEmailSender>(new StandInEmailSender(outbox));
var ffmpegPath = builder.Configuration["Audio:FfmpegPath"];
var ffmpegFound = await VerbaFlow.Infrastructure.Services.FfmpegAudioEnhancer.IsAvailableAsync(ffmpegPath);
// Recordings in several languages are sent in short pieces so each piece gets its own language (needs ffmpeg).
builder.Services.AddTransient<ISpeechService>(sp =>
{
    ISpeechService basic = speech.IsConfigured ? sp.GetRequiredService<VerbaFlow.Infrastructure.Azure.AzureSpeechService>() : sp.GetRequiredService<StandInSpeechService>();
    return ffmpegFound ? new VerbaFlow.Infrastructure.Services.LanguagePerPieceSpeech(basic, ffmpegPath) : basic;
});
if (ffmpegFound) builder.Services.AddSingleton<IAudioEnhancer>(_ => new VerbaFlow.Infrastructure.Services.FfmpegAudioEnhancer(ffmpegPath));
else builder.Services.AddSingleton<IAudioEnhancer, StandInAudioEnhancer>();
// Local speaker separation needs ffmpeg to read audio. Models are downloaded once into the data folder.
var diarizationOn = ffmpegFound && !string.Equals(builder.Configuration["Diarization:Enabled"], "false", StringComparison.OrdinalIgnoreCase);
if (diarizationOn)
{
    var diar = new VerbaFlow.Infrastructure.Services.DiarizationOptions(Path.Combine(dataDir, "models"), ffmpegPath,
        float.TryParse(builder.Configuration["Diarization:Threshold"], System.Globalization.CultureInfo.InvariantCulture, out var th) ? th : 0.8f,
        int.TryParse(builder.Configuration["Diarization:NumSpeakers"], out var ns) ? ns : -1,
        builder.Configuration["Diarization:EmbeddingModel"] is { Length: > 0 } em ? em : "titanet-small",
        string.Equals(builder.Configuration["Diarization:Audio"], "original", StringComparison.OrdinalIgnoreCase),
        float.TryParse(builder.Configuration["Diarization:MinSpeakerSeconds"], System.Globalization.CultureInfo.InvariantCulture, out var minSec) ? minSec : 8f,
        string.Equals(builder.Configuration["Diarization:Method"], "windowed", StringComparison.OrdinalIgnoreCase) ? "windowed" : "standard");
    builder.Services.AddSingleton(diar);
    builder.Services.AddHttpClient<VerbaFlow.Infrastructure.Services.SherpaSpeakerDiarizer>(c => c.Timeout = TimeSpan.FromMinutes(15));
    builder.Services.AddSingleton<ISpeakerDiarizer>(sp => sp.GetRequiredService<VerbaFlow.Infrastructure.Services.SherpaSpeakerDiarizer>());
}
builder.Services.AddSingleton<VerbaFlow.Infrastructure.Services.FailureReporter>();
builder.Services.AddSingleton<MeetingService>();
builder.Services.AddSingleton<VerbaFlow.Infrastructure.Export.ExportService>();
builder.Services.AddSingleton<VocabularyService>();
builder.Services.AddSingleton<SavedTextService>();
builder.Services.AddSingleton<LetterheadService>();
// Dictations go word for word through the Speech SDK when Azure Speech and ffmpeg are both there.
if (speech.IsConfigured && ffmpegFound)
    builder.Services.AddSingleton<IDictationSpeech>(_ => new VerbaFlow.Infrastructure.Azure.AzureWordForWordSpeech(speech, ffmpegPath));
// Speech mode: live text as the person speaks, through Azure AI Speech real-time recognition when it is set up.
if (speech.IsConfigured) builder.Services.AddSingleton<ILiveSpeech>(_ => new VerbaFlow.Infrastructure.Azure.AzureLiveSpeech(speech));
else builder.Services.AddSingleton<ILiveSpeech, VerbaFlow.Infrastructure.StandIns.StandInLiveSpeech>();
builder.Services.AddSingleton<LiveSessions>();
builder.Services.AddSingleton<ProcessingService>(sp => new ProcessingService(sp.GetRequiredService<Stores>(), sp.GetRequiredService<ISpeechService>(),
    sp.GetRequiredService<IAiOutputService>(), sp.GetRequiredService<IAudioEnhancer>(), sp.GetRequiredService<TimeProvider>(),
    sp.GetRequiredService<VerbaFlow.Infrastructure.Services.FailureReporter>(), sp.GetService<ISpeakerDiarizer>(), sp.GetService<VerbaFlow.Infrastructure.Services.DiarizationOptions>(),
    sp.GetService<IDictationSpeech>()));
builder.Services.AddHostedService<ProcessingWorker>();
// Meet: a note-taker joins online meetings (Zoom, Google Meet, Teams, Webex) through Recall.ai when it is set up.
var noteTaker = new VerbaFlow.Infrastructure.MeetingBots.RecallOptions(builder.Configuration["Meet:ApiKey"] ?? "",
    (builder.Configuration["Meet:Region"] ?? "").Trim().ToLowerInvariant(),
    builder.Configuration["Meet:NoteTakerName"] is { Length: > 0 } ntn ? ntn.Trim() : "VerbaFlow Notetaker");
if (noteTaker.IsConfigured)
{
    builder.Services.AddSingleton(noteTaker);
    builder.Services.AddHttpClient<INoteTakerService, VerbaFlow.Infrastructure.MeetingBots.RecallNoteTaker>(c => c.Timeout = TimeSpan.FromHours(2));
}
else builder.Services.AddSingleton<INoteTakerService>(sp => new StandInNoteTaker(sp.GetRequiredService<TimeProvider>(), ffmpegFound ? ffmpegPath : null));
builder.Services.AddSingleton(sp => new MeetService(sp.GetRequiredService<Stores>(), sp.GetRequiredService<INoteTakerService>(),
    sp.GetRequiredService<MeetingService>(), sp.GetRequiredService<TimeProvider>(), sp.GetRequiredService<VerbaFlow.Infrastructure.Services.FailureReporter>(),
    noteTaker.DisplayName));
builder.Services.AddSingleton<PlaybackLinks>();
// Meet: the note-taker goes to the online meetings in people's calendars (Microsoft 365 through Graph when the app has a
// client secret; any calendar through its private iCal address; a sample calendar while Microsoft 365 is not set up).
var graph = new VerbaFlow.Infrastructure.Calendars.GraphOptions((builder.Configuration["Entra:TenantId"] ?? "").Trim(),
    (builder.Configuration["Entra:ClientId"] ?? "").Trim(), builder.Configuration["Entra:ClientSecret"] ?? "");
if (graph.IsConfigured)
{
    builder.Services.AddSingleton(graph);
    builder.Services.AddHttpClient<VerbaFlow.Infrastructure.Calendars.GraphCalendarReader>(c => c.Timeout = TimeSpan.FromSeconds(60));
    builder.Services.AddSingleton<ICalendarReader>(sp => sp.GetRequiredService<VerbaFlow.Infrastructure.Calendars.GraphCalendarReader>());
}
else builder.Services.AddSingleton<ICalendarReader, VerbaFlow.Infrastructure.StandIns.SampleCalendarReader>();
builder.Services.AddSingleton<ICalendarReader>(_ => new VerbaFlow.Infrastructure.Calendars.LinkCalendarReader());
builder.Services.AddSingleton<CalendarService>();
if (!string.Equals(builder.Configuration["Meet:Worker"], "false", StringComparison.OrdinalIgnoreCase)) { builder.Services.AddHostedService<MeetWorker>(); builder.Services.AddHostedService<CalendarWorker>(); }

var signIn = SignInOptions.From(builder.Configuration, builder.Environment);
signIn.AddTo(builder.Services);

var app = builder.Build();
app.Logger.LogInformation(speech.IsConfigured
    ? "Speech: using Azure AI Speech at {Endpoint}"
    : "Speech: STAND-IN (placeholder text). Set Speech:Endpoint and Speech:Key to use Azure AI Speech.", speech.Endpoint);
app.Logger.LogInformation(speech.IsConfigured
    ? "Speech mode: live text through Azure AI Speech real-time recognition"
    : "Speech mode: STAND-IN live text (placeholder words). Set Speech:Endpoint and Speech:Key to use Azure AI Speech.");
app.Logger.LogInformation(ffmpegFound
    ? "Audio: ffmpeg found, so recordings are made mono, 16 kHz and levelled before transcription"
    : "Audio: ffmpeg NOT found, so recordings are sent as they are. Install ffmpeg to improve speaker separation (see docs/audio-preparation.md).");
app.Logger.LogInformation(diarizationOn
    ? "Speakers: using the local speaker-separation model (downloaded once into the data folder)"
    : "Speakers: using Azure's own speaker labels. Local speaker separation needs ffmpeg (see docs/speaker-separation.md).");
if (diarizationOn)
    _ = Task.Run(async () =>
    {
        try { await app.Services.GetRequiredService<VerbaFlow.Infrastructure.Services.SherpaSpeakerDiarizer>().EnsureModelsAsync(); app.Logger.LogInformation("Speakers: models are ready"); }
        catch (Exception ex) { app.Logger.LogWarning("Speakers: the models could not be downloaded yet ({Reason}). Azure's labels will be used until they can.", ex.Message); }
    });
app.Logger.LogInformation(emailOptions.IsConfigured
    ? "Email: sending through Azure Communication Services as {Sender}"
    : "Email: STAND-IN. Nothing is sent; emails are saved as .eml files in {Outbox}. Set Email:ConnectionString and Email:Sender to send for real.",
    emailOptions.IsConfigured ? emailOptions.Sender : outbox);
app.Logger.LogInformation(openAi.IsConfigured
    ? "Summaries and Check grammar: using Azure OpenAI deployment {Deployment}"
    : "Summaries and Check grammar: STAND-IN (placeholder text). Set OpenAI:Endpoint, OpenAI:Key and OpenAI:Deployment to use Azure OpenAI.", openAi.Deployment);

app.Logger.LogInformation(noteTaker.IsConfigured
    ? "Meet: the note-taker joins meetings through Recall.ai ({Region}) as \"{Name}\""
    : "Meet: STAND-IN note-taker (nobody joins; a sample recording comes back). Set Meet:ApiKey and Meet:Region to use Recall.ai (see docs/meet-setup.md).",
    noteTaker.Region, noteTaker.DisplayName);
app.Logger.LogInformation(graph.IsConfigured
    ? "Meet calendars: Microsoft 365 calendars read through Microsoft Graph, and calendar addresses"
    : "Meet calendars: calendar addresses, and a STAND-IN sample calendar. Set Entra:ClientSecret to read Microsoft 365 calendars (see docs/meet-setup.md).");
app.Logger.LogInformation("Database: {Database}; files in {DataDir}", database.Describe, dataDir);
app.Logger.LogInformation(signIn.Microsoft
    ? "Sign-in: Microsoft work accounts (Entra ID, directory {Tenant})" + (signIn.TestUsers ? ", and the test users while in Development" : "")
    : signIn.TestUsers ? "Sign-in: TEST USERS only (alice, bob, carol...). Set Entra:TenantId and Entra:ClientId to sign in with Microsoft (see docs/entra-sign-in-setup.md)."
    : "Sign-in: NOT SET UP. Nobody can sign in until Entra:TenantId and Entra:ClientId are set (see docs/entra-sign-in-setup.md).", signIn.TenantId);
if (signIn.TestUsers) await DevUsers.SeedAsync(app.Services.GetRequiredService<Stores>());

app.UseExceptionHandler(e => e.Run(async ctx =>
{
    var ex = ctx.Features.Get<Microsoft.AspNetCore.Diagnostics.IExceptionHandlerFeature>()?.Error;
    ctx.Response.StatusCode = ex switch
    {
        ForbiddenException => 403, NotFoundException => 404, DomainException => 400, _ => 500
    };
    ctx.Response.ContentType = "application/json";
    string message;
    if (ex is DomainException or ForbiddenException or NotFoundException) message = ex.Message;
    else
    {
        // Anything unexpected: the customer gets a plain message and a reference. Support gets the detail under the same reference.
        var rep = await ctx.RequestServices.GetRequiredService<VerbaFlow.Infrastructure.Services.FailureReporter>()
            .ReportAsync("request", null, ex ?? new InvalidOperationException("Unknown error"));
        message = CustomerMessages.For(FaultKind.Other, "VerbaFlow", rep.Reference);
    }
    await ctx.Response.WriteAsJsonAsync(new { error = message });
}));

app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(15) });
app.UseDefaultFiles();
app.UseStaticFiles();

// What the web app and the Widget need to know to sign in; public, so it sits outside the signed-in group.
app.MapGet("/api/sign-in", (SignInOptions o) => o.Describe());
// A Microsoft sign-in that failed in the browser: the reason goes to the support record, the person gets a reference.
// Public (the person is not signed in); short text only, and a few a minute at most.
var signInProblems = new SignInProblemLimit();
app.MapPost("/api/sign-in/problem", async (VerbaFlow.Infrastructure.Services.FailureReporter reporter, SignInProblemBody b) =>
{
    if (!signInProblems.Allow()) return Results.StatusCode(429);
    static string Cut(string? s, int n) => (s ?? "").Length > n ? s![..n] : s ?? "";
    var rep = await reporter.ReportAsync("sign-in", null, new InvalidOperationException($"Microsoft sign-in failed in the browser: {Cut(b.Code, 100)}: {Cut(b.Detail, 1000)}"));
    return Results.Ok(new { reference = rep.Reference });
});
// For Azure's health probe: answers only once the database can be reached. Public, says nothing else.
app.MapGet("/api/health", (Database db) =>
{
    try { using var c = db.Open(); return Results.Ok(new { ok = true }); }
    catch { return Results.Json(new { ok = false }, statusCode: 503); }
});
var api = app.MapGroup("/api");
api.AddEndpointFilter(SignIn.RequireUser);
api.MapMeetingEndpoints();
api.MapLiveEndpoints();
api.MapMeetEndpoints();
app.MapLiveSocket();
app.MapPlayback();
app.MapFallbackToFile("index.html");

app.Run();

public partial class Program;

public sealed record SignInProblemBody(string? Code, string? Detail);

/// <summary>At most 20 sign-in problem reports a minute, so the public endpoint cannot fill the support record.</summary>
public sealed class SignInProblemLimit
{
    private readonly Queue<DateTime> recent = new();
    public bool Allow()
    {
        lock (recent)
        {
            var now = DateTime.UtcNow;
            while (recent.Count > 0 && now - recent.Peek() > TimeSpan.FromMinutes(1)) recent.Dequeue();
            if (recent.Count >= 20) return false;
            recent.Enqueue(now);
            return true;
        }
    }
}
