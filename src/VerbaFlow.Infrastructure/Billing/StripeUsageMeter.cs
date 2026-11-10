using System.Net.Http.Headers;
using System.Text.Json;
using VerbaFlow.Core.Domain;
using VerbaFlow.Core.Providers;

namespace VerbaFlow.Infrastructure.Billing;

/// <summary>
/// Passes an invoiced organisation's seconds of speech to Stripe as meter events (Stripe Billing Meters). The organisation's
/// subscription in Stripe uses a metered price on that meter whose first tier (the included minutes) costs nothing, so only the
/// extra minutes appear on its next invoice. Each sending carries its own identifier, so Stripe counts a repeated one once.
/// </summary>
public sealed class StripeUsageMeter(HttpClient http, string secretKey, string eventName) : IUsageMeter
{
    public string Name => "stripe";
    public bool Pretend => false;

    public async Task SendAsync(string customerId, long seconds, DateTimeOffset at, string key, CancellationToken ct)
    {
        var form = new List<KeyValuePair<string, string>>
        {
            new("event_name", eventName),
            new("payload[stripe_customer_id]", customerId),
            new("payload[value]", seconds.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            new("timestamp", at.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture)),
            new("identifier", key),
        };
        using var req = new HttpRequestMessage(HttpMethod.Post, StripePayments.Api + "billing/meter_events") { Content = new FormUrlEncodedContent(form) };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", secretKey);
        req.Headers.Add("Idempotency-Key", key);
        using var res = await http.SendAsync(req, ct);
        if (res.IsSuccessStatusCode) return;
        var text = await res.Content.ReadAsStringAsync(ct);
        string detail;
        try
        {
            var err = JsonDocument.Parse(text).RootElement.GetProperty("error");
            // Already counted under this identifier: a repeat of a sending that went through.
            if (err.TryGetProperty("code", out var c) && c.GetString() is "duplicate_meter_event" or "idempotency_key_in_use") return;
            detail = err.GetProperty("message").GetString() ?? "";
        }
        catch { detail = text.Length > 300 ? text[..300] : text; }
        throw new ProviderException(FailureReporterKinds.ForStatus((int)res.StatusCode), $"Stripe meter event answered {(int)res.StatusCode}: {detail}");
    }
}

/// <summary>Keeps the organisation's sendings in memory, used until Stripe is set up (nothing is invoiced).</summary>
public sealed class StandInUsageMeter : IUsageMeter
{
    private readonly List<(string Customer, long Seconds, DateTimeOffset At, string Key)> sent = [];
    public string Name => "stand-in";
    public bool Pretend => true;
    public IReadOnlyList<(string Customer, long Seconds, DateTimeOffset At, string Key)> Sent { get { lock (sent) return [.. sent]; } }

    public Task SendAsync(string customerId, long seconds, DateTimeOffset at, string key, CancellationToken ct)
    {
        lock (sent) if (!sent.Any(s => s.Key == key)) sent.Add((customerId, seconds, at, key));
        return Task.CompletedTask;
    }
}
