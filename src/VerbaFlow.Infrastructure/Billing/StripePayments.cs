using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using VerbaFlow.Core.Domain;
using VerbaFlow.Core.Providers;

namespace VerbaFlow.Infrastructure.Billing;

public sealed record StripeOptions(string SecretKey, string WebhookSecret)
{
    public bool IsConfigured => SecretKey.Length > 0 && WebhookSecret.Length > 0;
}

/// <summary>
/// Card payments through Stripe: Stripe Checkout for paying (a monthly subscription), Stripe's customer portal for changing the card
/// or cancelling, and Stripe's signed notices (webhooks) to learn when a plan starts, renews, fails to renew or ends. VerbaFlow never
/// sees card details. Each plan's StripePriceId is the price made for it in Stripe.
/// </summary>
public sealed class StripePayments(HttpClient http, StripeOptions options, Plans plans, TimeProvider clock) : IPayments
{
    public const string Api = "https://api.stripe.com/v1/";
    /// <summary>How old a notice may be (Stripe's own advice is 5 minutes), so a copied one cannot be replayed later.</summary>
    public static readonly TimeSpan Tolerance = TimeSpan.FromMinutes(5);
    public string Name => "stripe";
    public bool Pretend => false;

    public async Task<PaymentPage> CheckoutAsync(User user, Guid account, Plan plan, string returnUrl, CancellationToken ct)
    {
        var form = new List<KeyValuePair<string, string>>
        {
            new("mode", "subscription"),
            new("line_items[0][price]", plan.StripePriceId ?? throw new InvalidOperationException($"Plan {plan.Id} has no Stripe price.")),
            new("line_items[0][quantity]", "1"),
            new("success_url", returnUrl + "?paid=1"),
            new("cancel_url", returnUrl),
            new("client_reference_id", account.ToString()),
            new("metadata[account]", account.ToString()),
            new("metadata[userId]", user.Id.ToString()),
            new("metadata[plan]", plan.Id),
            new("subscription_data[metadata][account]", account.ToString()),
            new("subscription_data[metadata][plan]", plan.Id),
        };
        if (user.Email.Contains('@')) form.Add(new("customer_email", user.Email));
        var doc = await PostAsync("checkout/sessions", form, ct);
        return new PaymentPage(doc.GetProperty("url").GetString()!, doc.GetProperty("id").GetString()!);
    }

    public async Task<PaymentPage> ManageAsync(PlanRecord record, string returnUrl, CancellationToken ct)
    {
        var doc = await PostAsync("billing_portal/sessions", [new("customer", record.CustomerId!), new("return_url", returnUrl)], ct);
        return new PaymentPage(doc.GetProperty("url").GetString()!, doc.GetProperty("id").GetString()!);
    }

    private async Task<JsonElement> PostAsync(string path, IEnumerable<KeyValuePair<string, string>> form, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, Api + path) { Content = new FormUrlEncodedContent(form) };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.SecretKey);
        using var res = await http.SendAsync(req, ct);
        var text = await res.Content.ReadAsStringAsync(ct);
        if (!res.IsSuccessStatusCode)
        {
            // Stripe's own error text is for support only; the key is never in it.
            string detail;
            try { detail = JsonDocument.Parse(text).RootElement.GetProperty("error").GetProperty("message").GetString() ?? ""; }
            catch { detail = text.Length > 300 ? text[..300] : text; }
            throw new ProviderException(FailureReporterKinds.ForStatus((int)res.StatusCode), $"Stripe {path} answered {(int)res.StatusCode}: {detail}");
        }
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    /// <summary>Checks Stripe's signature (header Stripe-Signature: t=…,v1=…) over the exact body.</summary>
    public static bool Verify(string body, string? header, string secret, DateTimeOffset now)
    {
        if (string.IsNullOrEmpty(header)) return false;
        long? t = null; var sigs = new List<string>();
        foreach (var part in header.Split(','))
        {
            var kv = part.Split('=', 2);
            if (kv.Length != 2) continue;
            if (kv[0].Trim() == "t" && long.TryParse(kv[1], out var ts)) t = ts;
            else if (kv[0].Trim() == "v1") sigs.Add(kv[1].Trim());
        }
        if (t is null || sigs.Count == 0) return false;
        if ((now - DateTimeOffset.FromUnixTimeSeconds(t.Value)).Duration() > Tolerance) return false;
        var mac = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes($"{t}.{body}"));
        var expected = Convert.ToHexString(mac).ToLowerInvariant();
        return sigs.Any(s => CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(s), Encoding.ASCII.GetBytes(expected)));
    }

    public PaymentNotice? Read(string body, string? signature)
    {
        if (!Verify(body, signature, options.WebhookSecret, clock.GetUtcNow())) return null;
        return Parse(body, plans);
    }

    /// <summary>What a (checked) Stripe event means for a plan. Events that change nothing come back as kind "ignored".</summary>
    public static PaymentNotice Parse(string body, Plans plans)
    {
        var root = JsonDocument.Parse(body).RootElement;
        var type = root.GetProperty("type").GetString() ?? "";
        var o = root.GetProperty("data").GetProperty("object");
        static string? Str(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        static Guid? AccountOf(JsonElement e) => e.TryGetProperty("metadata", out var m) && m.ValueKind == JsonValueKind.Object
            && Guid.TryParse(Str(m, "account"), out var g) ? g : null;
        switch (type)
        {
            case "checkout.session.completed":
                if (Str(o, "mode") != "subscription") return Ignored(type);
                var account = AccountOf(o) ?? (Guid.TryParse(Str(o, "client_reference_id"), out var c) ? c : null);
                var planId = o.TryGetProperty("metadata", out var md) && md.ValueKind == JsonValueKind.Object ? Str(md, "plan") : null;
                return new PaymentNotice(type, account, planId, PlanStatus.Active, Str(o, "customer"), Str(o, "subscription"), null, null);
            case "customer.subscription.created" or "customer.subscription.updated" or "customer.subscription.deleted":
                var status = type.EndsWith(".deleted") ? PlanStatus.Cancelled : Str(o, "status") switch
                {
                    "active" or "trialing" => PlanStatus.Active,
                    "past_due" or "unpaid" => PlanStatus.PastDue,
                    "canceled" or "incomplete_expired" => PlanStatus.Cancelled,
                    _ => (PlanStatus?)null,
                };
                if (status is null) return Ignored(type); // "incomplete": the first payment has not gone through yet
                string? price = null; long? end = o.TryGetProperty("current_period_end", out var pe) && pe.ValueKind == JsonValueKind.Number ? pe.GetInt64() : null;
                if (o.TryGetProperty("items", out var items) && items.TryGetProperty("data", out var data) && data.GetArrayLength() > 0)
                {
                    var first = data[0];
                    if (first.TryGetProperty("price", out var p)) price = Str(p, "id");
                    // Newer Stripe versions keep the period on the item.
                    if (end is null && first.TryGetProperty("current_period_end", out var ie) && ie.ValueKind == JsonValueKind.Number) end = ie.GetInt64();
                }
                var plan = plans.All.FirstOrDefault(x => x.StripePriceId != null && x.StripePriceId == price)?.Id;
                var cancelLater = o.TryGetProperty("cancel_at_period_end", out var ca) && ca.ValueKind == JsonValueKind.True;
                return new PaymentNotice(type, AccountOf(o), plan, status, Str(o, "customer"), Str(o, "id"),
                    end is { } s ? DateTimeOffset.FromUnixTimeSeconds(s) : null, cancelLater);
            default:
                return Ignored(type);
        }
    }

    private static PaymentNotice Ignored(string type) => new("ignored:" + type, null, null, null, null, null, null, null);
}

/// <summary>How an HTTP status from a provider is classed (same rule as FailureReporter.ForStatus).</summary>
internal static class FailureReporterKinds
{
    public static FaultKind ForStatus(int code) => Services.FailureReporter.ForStatus((System.Net.HttpStatusCode)code);
}
