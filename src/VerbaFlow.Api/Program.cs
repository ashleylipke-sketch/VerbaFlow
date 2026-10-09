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
builder.Services.AddSingleton(new SqliteDatabase(Path.Combine(dataDir, "verbaflow.db")));
builder.Services.AddSingleton(sp => new Stores(sp.GetRequiredService<SqliteDatabase>(), Path.Combine(dataDir, "media"), sp.GetRequiredService<TimeProvider>()));
builder.Services.AddSingleton<ProcessingQueue>();
// Stand-ins: swapped for Azure services behind these same interfaces.
var speech = new VerbaFlow.Infrastructure.Azure.AzureSpeechOptions(
    builder.Configuration["Speech:Endpoint"] ?? "", builder.Configuration["Speech:Key"] ?? "");
if (speech.IsConfigured)
{
    builder.Services.AddSingleton(speech);
    builder.Services.AddHttpClient<ISpeechService, VerbaFlow.Infrastructure.Azure.AzureSpeechService>(c => c.Timeout = TimeSpan.FromMinutes(30));
}
else builder.Services.AddSingleton<ISpeechService, StandInSpeechService>();
var openAi = new VerbaFlow.Infrastructure.Azure.AzureOpenAiOptions(builder.Configuration["OpenAI:Endpoint"] ?? "",
    builder.Configuration["OpenAI:Key"] ?? "", builder.Configuration["OpenAI:Deployment"] ?? "");
if (openAi.IsConfigured)
{
    builder.Services.AddSingleton(openAi);
    builder.Services.AddHttpClient<IAiOutputService, VerbaFlow.Infrastructure.Azure.AzureOpenAiOutputService>(c => c.Timeout = TimeSpan.FromMinutes(10));
}
else builder.Services.AddSingleton<IAiOutputService, StandInAiOutputService>();
builder.Services.AddSingleton<IMalwareScanner, StandInMalwareScanner>();
var ffmpegPath = builder.Configuration["Audio:FfmpegPath"];
var ffmpegFound = await VerbaFlow.Infrastructure.Services.FfmpegAudioEnhancer.IsAvailableAsync(ffmpegPath);
if (ffmpegFound) builder.Services.AddSingleton<IAudioEnhancer>(_ => new VerbaFlow.Infrastructure.Services.FfmpegAudioEnhancer(ffmpegPath));
else builder.Services.AddSingleton<IAudioEnhancer, StandInAudioEnhancer>();
// Local speaker separation needs ffmpeg to read audio. Models are downloaded once into the data folder.
var diarizationOn = ffmpegFound && !string.Equals(builder.Configuration["Diarization:Enabled"], "false", StringComparison.OrdinalIgnoreCase);
if (diarizationOn)
{
    var diar = new VerbaFlow.Infrastructure.Services.DiarizationOptions(Path.Combine(dataDir, "models"), ffmpegPath,
        float.TryParse(builder.Configuration["Diarization:Threshold"], System.Globalization.CultureInfo.InvariantCulture, out var th) ? th : 0.8f,
        int.TryParse(builder.Configuration["Diarization:NumSpeakers"], out var ns) ? ns : -1,
        builder.Configuration["Diarization:EmbeddingModel"] is { Length: > 0 } em ? em : "titanet-small");
    builder.Services.AddSingleton(diar);
    builder.Services.AddHttpClient<VerbaFlow.Infrastructure.Services.SherpaSpeakerDiarizer>(c => c.Timeout = TimeSpan.FromMinutes(15));
    builder.Services.AddSingleton<ISpeakerDiarizer>(sp => sp.GetRequiredService<VerbaFlow.Infrastructure.Services.SherpaSpeakerDiarizer>());
}
builder.Services.AddSingleton<VerbaFlow.Infrastructure.Services.FailureReporter>();
builder.Services.AddSingleton<MeetingService>();
builder.Services.AddSingleton<VocabularyService>();
builder.Services.AddSingleton<ProcessingService>();
builder.Services.AddHostedService<ProcessingWorker>();

var app = builder.Build();
app.Logger.LogInformation(speech.IsConfigured
    ? "Speech: using Azure AI Speech at {Endpoint}"
    : "Speech: STAND-IN (placeholder text). Set Speech:Endpoint and Speech:Key to use Azure AI Speech.", speech.Endpoint);
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
app.Logger.LogInformation(openAi.IsConfigured
    ? "Summaries: using Azure OpenAI deployment {Deployment}"
    : "Summaries: STAND-IN (placeholder text). Set OpenAI:Endpoint, OpenAI:Key and OpenAI:Deployment to use Azure OpenAI.", openAi.Deployment);

await DevUsers.SeedAsync(app.Services.GetRequiredService<Stores>());

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

app.UseDefaultFiles();
app.UseStaticFiles();

var api = app.MapGroup("/api");
api.AddEndpointFilter(DevUsers.RequireUser);
api.MapMeetingEndpoints();
app.MapFallbackToFile("index.html");

app.Run();

public partial class Program;
