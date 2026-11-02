using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using VerbaFlow.Core.Domain;
using VerbaFlow.Core.Providers;

namespace VerbaFlow.Infrastructure.MeetingBots;

/// <param name="ApiKey">From the Recall.ai dashboard. A secret: user-secrets or the online app's secrets only.</param>
/// <param name="Region">The region the Recall.ai account is in: us-west-2, us-east-1, eu-central-1 or ap-northeast-1.</param>
/// <param name="DisplayName">The name the note-taker shows in the meeting.</param>
public sealed record RecallOptions(string ApiKey, string Region, string DisplayName = "VerbaFlow Notetaker")
{
    public static readonly string[] Regions = ["us-west-2", "us-east-1", "eu-central-1", "ap-northeast-1"];
    public bool IsConfigured => !string.IsNullOrWhiteSpace(ApiKey) && Regions.Contains(Region);
    public Uri BaseAddress => new($"https://{Region}.recall.ai/api/v1/");
}

/// <summary>
/// The note-taker through Recall.ai's meeting bot API (docs.recall.ai): one bot per meeting, which joins from the link,
/// records the meeting as a mixed MP4 and keeps the platform's speaker timeline. See docs/meet-setup.md.
/// </summary>
public sealed class RecallNoteTaker(HttpClient http, RecallOptions options) : INoteTakerService
{
    public string Name => "recall-ai";

    /// <summary>Recall asks for bots to be booked at least this far ahead; nearer than this they are sent straight away.</summary>
    public static readonly TimeSpan BookAhead = TimeSpan.FromMinutes(10);

    public async Task<string> ScheduleAsync(NoteTakerRequest r, CancellationToken ct)
    {
        var body = new JsonObject
        {
            ["meeting_url"] = r.MeetingUrl,
            ["bot_name"] = r.DisplayName,
            ["metadata"] = new JsonObject { ["verbaflow"] = r.Reference.ToString() },
            // audio_separate_mp3: each participant's own microphone as well (Zoom, Teams, Google Meet), so their words can be
            // transcribed from their own audio instead of being shared out from the mixed sound.
            ["recording_config"] = new JsonObject { ["video_mixed_mp4"] = new JsonObject(), ["participant_events"] = new JsonObject(), ["audio_separate_mp3"] = new JsonObject() },
            ["chat"] = new JsonObject { ["on_bot_join"] = new JsonObject { ["send_to"] = "everyone", ["message"] = r.Announcement } },
        };
        if (r.JoinAt is { } at && at - DateTimeOffset.UtcNow > BookAhead) body["join_at"] = at.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
        JsonNode? json;
        try { json = await SendAsync(HttpMethod.Post, "bot/", body, ct); }
        catch (ProviderException ex) when (ex.Message.Contains("audio_separate", StringComparison.OrdinalIgnoreCase))
        {
            // An account or platform that refuses separate audio still gets a note-taker; the mixed sound is used.
            ((JsonObject)body["recording_config"]!).Remove("audio_separate_mp3");
            json = await SendAsync(HttpMethod.Post, "bot/", body, ct);
        }
        return json?["id"]?.GetValue<string>() ?? throw new ProviderException(FaultKind.Other, "Recall.ai gave no bot id.");
    }

    public async Task<NoteTakerStatus> GetAsync(string botId, CancellationToken ct)
    {
        var bot = await SendAsync(HttpMethod.Get, $"bot/{Uri.EscapeDataString(botId)}/", null, ct)
            ?? throw new ProviderException(FaultKind.Other, "Recall.ai returned an empty bot.");
        return Read(bot);
    }

    /// <summary>Works out the phase from a Retrieve Bot answer. Public so it can be checked against recorded answers.</summary>
    public static NoteTakerStatus Read(JsonNode bot)
    {
        var last = (bot["status_changes"] as JsonArray)?.LastOrDefault();
        var code = last?["code"]?.GetValue<string>() ?? "ready";
        var sub = last?["sub_code"]?.GetValue<string>();
        var detail = sub is null ? code : $"{code} ({sub})";
        var recording = (bot["recordings"] as JsonArray)?.LastOrDefault();
        var shortcuts = recording?["media_shortcuts"];
        var video = shortcuts?["video_mixed"];
        var videoUrl = video?["status"]?["code"]?.GetValue<string>() == "done" ? video["data"]?["download_url"]?.GetValue<string>() : null;
        var speakers = shortcuts?["participant_events"]?["data"]?["speaker_timeline_download_url"]?.GetValue<string>();
        var recordingId = recording?["id"]?.GetValue<string>();
        return code switch
        {
            "joining_call" => new(NoteTakerPhase.Joining, detail),
            "in_waiting_room" or "recording_permission_denied" => new(NoteTakerPhase.WaitingToBeLetIn, detail),
            "in_call_not_recording" or "recording_permission_allowed" => new(NoteTakerPhase.Joining, detail),
            "in_call_recording" => new(NoteTakerPhase.Recording, detail),
            "call_ended" => new(NoteTakerPhase.MeetingEnded, detail),
            "done" or "analysis_done" or "analysis_failed" => videoUrl is not null ? new(NoteTakerPhase.Ready, detail, videoUrl, speakers, recordingId)
                : recording is null ? new(NoteTakerPhase.Failed, detail + ": no recording was made")
                : video?["status"]?["code"]?.GetValue<string>() is "failed" ? new(NoteTakerPhase.Failed, detail + ": the video failed")
                : new(NoteTakerPhase.MeetingEnded, detail),
            "fatal" or "media_expired" => new(NoteTakerPhase.Failed, detail),
            "ready" or "scheduled" => new(NoteTakerPhase.Scheduled, detail),
            _ => new(NoteTakerPhase.Unknown, detail), // breakout rooms and codes Recall.ai adds later
        };
    }

    public async Task CancelAsync(string botId, CancellationToken ct)
    {
        var status = await GetAsync(botId, ct);
        var id = Uri.EscapeDataString(botId);
        if (status.Phase == NoteTakerPhase.Scheduled) await SendAsync(HttpMethod.Delete, $"bot/{id}/", null, ct);
        else if (status.Phase is NoteTakerPhase.Joining or NoteTakerPhase.WaitingToBeLetIn or NoteTakerPhase.Recording)
            await SendAsync(HttpMethod.Post, $"bot/{id}/leave_call/", null, ct);
    }

    public async Task ForgetAsync(string botId, CancellationToken ct) =>
        await SendAsync(HttpMethod.Post, $"bot/{Uri.EscapeDataString(botId)}/delete_media/", null, ct);

    public async Task<Stream> DownloadVideoAsync(string url, CancellationToken ct)
    {
        // A pre-signed link: fetched without the API key.
        var res = await http.GetAsync(Checked(url), HttpCompletionOption.ResponseHeadersRead, ct);
        if (!res.IsSuccessStatusCode) throw new ProviderException(FailureFor(res.StatusCode), $"Recall.ai video download returned {(int)res.StatusCode}.");
        return await res.Content.ReadAsStreamAsync(ct);
    }

    public async Task<IReadOnlyList<PlatformSpeech>> DownloadSpeakersAsync(string url, CancellationToken ct)
    {
        using var res = await http.GetAsync(Checked(url), ct);
        if (!res.IsSuccessStatusCode) throw new ProviderException(FailureFor(res.StatusCode), $"Recall.ai speaker timeline download returned {(int)res.StatusCode}.");
        return ReadSpeakers(await res.Content.ReadAsStringAsync(ct));
    }

    /// <summary>The speaker timeline download: participant, start and end (seconds from the start of the recording).</summary>
    public static IReadOnlyList<PlatformSpeech> ReadSpeakers(string json)
    {
        var list = new List<PlatformSpeech>();
        if (JsonNode.Parse(json) is not JsonArray items) return list;
        foreach (var e in items)
        {
            var p = e?["participant"];
            var start = e?["start_timestamp"]?["relative"]?.GetValue<double>();
            var end = e?["end_timestamp"]?["relative"]?.GetValue<double>() ?? start;
            if (p?["id"] is null || start is null || end is null) continue;
            var id = (int)p["id"]!.GetValue<double>();
            var name = p["name"]?.GetValue<string>();
            list.Add(new PlatformSpeech((int)(start.Value * 1000), (int)(end.Value * 1000), id,
                string.IsNullOrWhiteSpace(name) ? $"Participant {id}" : name.Trim()));
        }
        return list;
    }

    public async Task<PersonAudio> ListPersonAudioAsync(string botId, NoteTakerStatus status, CancellationToken ct)
    {
        if (status.RecordingId is null) return new PersonAudio(true, [], "no recording id");
        var list = await SendAsync(HttpMethod.Get, $"audio_separate/?recording_id={Uri.EscapeDataString(status.RecordingId)}", null, ct);
        var results = (list?["results"] as JsonArray ?? list as JsonArray)?.Where(r => r is not null).ToList() ?? [];
        if (results.Count == 0) return new PersonAudio(true, [], "none made"); // booked before it was asked for, or a platform without it
        var latest = results[^1]!;
        var code = latest["status"]?["code"]?.GetValue<string>();
        if (code == "failed") return new PersonAudio(true, [], "failed: " + latest["status"]?["sub_code"]?.GetValue<string>());
        if (code != "done") return new PersonAudio(false, [], code);
        var url = latest["data"]?["download_url"]?.GetValue<string>();
        if (url is null) return new PersonAudio(true, [], "done but no download link");
        using var res = await http.GetAsync(Checked(url), ct);
        if (!res.IsSuccessStatusCode) throw new ProviderException(FailureFor(res.StatusCode), $"Recall.ai separate audio list download returned {(int)res.StatusCode}.");
        var format = latest["format"]?.GetValue<string>() ?? "mp3";
        return new PersonAudio(true, ReadPersonAudio(await res.Content.ReadAsStringAsync(ct), format));
    }

    /// <summary>
    /// The separate audio's parts list: per part the participant, where it starts (seconds from the start of the recording),
    /// how long it lasts and a pre-signed download link.
    /// </summary>
    public static IReadOnlyList<PersonAudioDownload> ReadPersonAudio(string json, string format = "mp3")
    {
        var list = new List<PersonAudioDownload>();
        if (JsonNode.Parse(json) is not JsonArray items) return list;
        foreach (var e in items)
        {
            var p = e?["participant"];
            var url = e?["download_url"]?.GetValue<string>();
            var start = Number(e?["start_timestamp"]?["relative"]) ?? Number(e?["start_timestamp"]);
            var end = Number(e?["end_timestamp"]?["relative"]);
            var duration = Number(e?["duration"]) ?? (end is not null && start is not null ? end - start : null);
            if (p?["id"] is null || url is null || start is null) continue;
            var id = (int)Number(p["id"])!.Value;
            var name = p["name"]?.GetValue<string>();
            list.Add(new PersonAudioDownload(id, string.IsNullOrWhiteSpace(name) ? $"Participant {id}" : name.Trim(),
                (int)(start.Value * 1000), (int)((duration ?? 0) * 1000), url, format));
        }
        return list;
    }

    private static double? Number(JsonNode? n) => n is JsonValue v ? v.TryGetValue<double>(out var d) ? d
        : v.TryGetValue<string>(out var s) && double.TryParse(s, System.Globalization.CultureInfo.InvariantCulture, out d) ? d : null : null;

    public async Task<Stream> DownloadPersonAudioAsync(PersonAudioDownload part, CancellationToken ct)
    {
        var res = await http.GetAsync(Checked(part.Url), HttpCompletionOption.ResponseHeadersRead, ct);
        if (!res.IsSuccessStatusCode) throw new ProviderException(FailureFor(res.StatusCode), $"Recall.ai separate audio download returned {(int)res.StatusCode}.");
        return await res.Content.ReadAsStreamAsync(ct);
    }

    private static Uri Checked(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps ? u
            : throw new ProviderException(FaultKind.Other, "Recall.ai gave a download link that is not https.");

    private async Task<JsonNode?> SendAsync(HttpMethod method, string path, JsonNode? body, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(method, new Uri(options.BaseAddress, path));
        req.Headers.Authorization = new AuthenticationHeaderValue("Token", options.ApiKey);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (body is not null) req.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        using var res = await http.SendAsync(req, ct);
        var text = await res.Content.ReadAsStringAsync(ct);
        if (!res.IsSuccessStatusCode)
            throw new ProviderException(FailureFor(res.StatusCode), $"Recall.ai {method} {path} returned {(int)res.StatusCode}: {(text.Length > 500 ? text[..500] : text)}");
        return text.Length == 0 ? null : JsonNode.Parse(text);
    }

    private static FaultKind FailureFor(HttpStatusCode code) => code switch
    {
        HttpStatusCode.TooManyRequests or (HttpStatusCode)507 => FaultKind.Busy,
        HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => FaultKind.Settings,
        >= HttpStatusCode.InternalServerError => FaultKind.Unavailable,
        _ => FaultKind.Other,
    };
}
