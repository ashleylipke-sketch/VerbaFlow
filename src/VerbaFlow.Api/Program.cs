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
builder.Services.AddSingleton<IAiOutputService, StandInAiOutputService>();
builder.Services.AddSingleton<IMalwareScanner, StandInMalwareScanner>();
builder.Services.AddSingleton<IAudioEnhancer, StandInAudioEnhancer>();
builder.Services.AddSingleton<MeetingService>();
builder.Services.AddSingleton<ProcessingService>();
builder.Services.AddHostedService<ProcessingWorker>();

var app = builder.Build();
app.Logger.LogInformation(speech.IsConfigured
    ? "Speech: using Azure AI Speech at {Endpoint}"
    : "Speech: STAND-IN (placeholder text). Set Speech:Endpoint and Speech:Key to use Azure AI Speech.", speech.Endpoint);

await DevUsers.SeedAsync(app.Services.GetRequiredService<Stores>());

app.UseExceptionHandler(e => e.Run(async ctx =>
{
    var ex = ctx.Features.Get<Microsoft.AspNetCore.Diagnostics.IExceptionHandlerFeature>()?.Error;
    ctx.Response.StatusCode = ex switch
    {
        ForbiddenException => 403, NotFoundException => 404, DomainException => 400, _ => 500
    };
    ctx.Response.ContentType = "application/json";
    var message = ex is DomainException or ForbiddenException or NotFoundException ? ex.Message : "Something went wrong.";
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
