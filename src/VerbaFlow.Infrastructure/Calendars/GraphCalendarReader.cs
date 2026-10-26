using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using VerbaFlow.Core.Domain;
using VerbaFlow.Core.Providers;

namespace VerbaFlow.Infrastructure.Calendars;

/// <param name="ClientSecret">The VerbaFlow app registration's client secret (a secret: user-secrets or a Container App secret).</param>
public sealed record GraphOptions(string TenantId, string ClientId, string ClientSecret)
{
    public bool IsConfigured => TenantId.Length > 0 && ClientId.Length > 0 && ClientSecret.Length > 0;
}

/// <summary>
/// Reads a person's Microsoft 365 (Outlook) calendar through Microsoft Graph as the VerbaFlow app itself (application
/// permission Calendars.Read, granted once by an administrator), so the calendar can be checked when the person is not
/// signed in. Only people who sign in with Microsoft are read, by their Microsoft id.
/// </summary>
public sealed class GraphCalendarReader(HttpClient http, GraphOptions options, TimeProvider clock) : ICalendarReader
{
    public string Source => CalendarSources.Microsoft;
    private const int MaxPages = 5;
    private readonly SemaphoreSlim tokenGate = new(1, 1);
    private (string Token, DateTimeOffset Until)? token;

    public async Task<IReadOnlyList<CalendarMeeting>> ReadAsync(CalendarSettings settings, User user, DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(user.EntraObjectId)) throw new ProviderException(FaultKind.Settings, "This person does not sign in with Microsoft, so there is no Microsoft 365 calendar to read.");
        var select = "id,subject,start,end,isCancelled,isAllDay,location,locations,onlineMeeting,body,responseStatus,seriesMasterId,originalStart,type";
        string? next = $"https://graph.microsoft.com/v1.0/users/{Uri.EscapeDataString(user.EntraObjectId)}/calendarView" +
            $"?startDateTime={Uri.EscapeDataString(from.UtcDateTime.ToString("o"))}&endDateTime={Uri.EscapeDataString(to.UtcDateTime.ToString("o"))}" +
            $"&$select={select}&$top=100&$orderby=start/dateTime";
        var list = new List<CalendarMeeting>();
        for (var page = 0; next is not null && page < MaxPages; page++)
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, next);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await TokenAsync(ct));
            req.Headers.Add("Prefer", "outlook.timezone=\"UTC\", outlook.body-content-type=\"text\"");
            using var res = await http.SendAsync(req, ct);
            var body = await res.Content.ReadAsStringAsync(ct);
            if (!res.IsSuccessStatusCode) throw Fault(res.StatusCode, body);
            var json = JsonNode.Parse(body)!;
            foreach (var e in json["value"]?.AsArray() ?? []) if (e is not null && Read(e) is { } m) list.Add(m);
            next = json["@odata.nextLink"]?.GetValue<string>();
        }
        return list;
    }

    /// <summary>One Graph event as a meeting; null for cancelled and all-day entries.</summary>
    public static CalendarMeeting? Read(JsonNode e)
    {
        if (e["isCancelled"]?.GetValue<bool>() == true || e["isAllDay"]?.GetValue<bool>() == true) return null;
        var start = Utc(e["start"]?["dateTime"]?.GetValue<string>());
        var end = Utc(e["end"]?["dateTime"]?.GetValue<string>());
        if (start is null || end is null) return null;
        // An occurrence of a repeating meeting keeps its key when only that occurrence is moved.
        var master = e["seriesMasterId"]?.GetValue<string>();
        var original = e["originalStart"]?.GetValue<string>();
        var key = master is not null && original is not null ? $"ms:{master}|{DateTimeOffset.Parse(original, CultureInfo.InvariantCulture).UtcDateTime:yyyyMMddTHHmmss}"
            : $"ms:{e["id"]?.GetValue<string>()}";
        var locations = string.Join(" ", (e["locations"]?.AsArray() ?? []).Select(l => l?["displayName"]?.GetValue<string>() + " " + l?["locationUri"]?.GetValue<string>()));
        var link = MeetingLinks.Find(e["onlineMeeting"]?["joinUrl"]?.GetValue<string>(), e["location"]?["displayName"]?.GetValue<string>(),
            locations, e["body"]?["content"]?.GetValue<string>());
        var declined = string.Equals(e["responseStatus"]?["response"]?.GetValue<string>(), "declined", StringComparison.OrdinalIgnoreCase);
        var subject = e["subject"]?.GetValue<string>() is { Length: > 0 } s ? s.Trim() : "Meeting";
        return new CalendarMeeting(key, subject, start.Value, end.Value, link, declined);
    }

    private static DateTimeOffset? Utc(string? s) =>
        DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var d)
            ? new DateTimeOffset(d, TimeSpan.Zero) : null;

    private static ProviderException Fault(HttpStatusCode status, string body)
    {
        var detail = body.Length > 600 ? body[..600] : body;
        return status switch
        {
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => new ProviderException(FaultKind.Settings, $"Microsoft Graph refused to read the calendar ({(int)status}): {detail}"),
            HttpStatusCode.NotFound => new ProviderException(FaultKind.Settings, $"Microsoft Graph found no calendar for this person (404): {detail}"),
            HttpStatusCode.TooManyRequests => new ProviderException(FaultKind.Busy, $"Microsoft Graph is busy (429): {detail}"),
            >= HttpStatusCode.InternalServerError => new ProviderException(FaultKind.Unavailable, $"Microsoft Graph failed ({(int)status}): {detail}"),
            _ => new ProviderException(FaultKind.Other, $"Microsoft Graph answered {(int)status}: {detail}"),
        };
    }

    /// <summary>An app-only token for Microsoft Graph, kept until five minutes before it runs out.</summary>
    private async Task<string> TokenAsync(CancellationToken ct)
    {
        await tokenGate.WaitAsync(ct);
        try
        {
            if (token is { } t && t.Until > clock.GetUtcNow()) return t.Token;
            using var res = await http.PostAsync($"https://login.microsoftonline.com/{Uri.EscapeDataString(options.TenantId)}/oauth2/v2.0/token",
                new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["client_id"] = options.ClientId,
                    ["client_secret"] = options.ClientSecret,
                    ["scope"] = "https://graph.microsoft.com/.default",
                    ["grant_type"] = "client_credentials",
                }), ct);
            var body = await res.Content.ReadAsStringAsync(ct);
            if (!res.IsSuccessStatusCode)
            {
                // Never pass on the request (it holds the secret); Microsoft's answer names the problem (e.g. AADSTS7000215).
                var err = JsonNode.Parse(body)?["error_description"]?.GetValue<string>() ?? $"status {(int)res.StatusCode}";
                throw new ProviderException(FaultKind.Settings, "Microsoft sign-in refused VerbaFlow's app token for calendars: " + (err.Length > 400 ? err[..400] : err));
            }
            var json = JsonNode.Parse(body)!;
            var value = json["access_token"]!.GetValue<string>();
            var seconds = json["expires_in"]?.GetValue<int>() ?? 3600;
            token = (value, clock.GetUtcNow().AddSeconds(Math.Max(60, seconds - 300)));
            return value;
        }
        finally { tokenGate.Release(); }
    }
}
