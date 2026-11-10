using System.Security.Cryptography;
using System.Text;
using VerbaFlow.Core.Domain;
using VerbaFlow.Core.Providers;
using VerbaFlow.Infrastructure.Billing;
using VerbaFlow.Infrastructure.Services;
using VerbaFlow.Infrastructure.StandIns;

namespace VerbaFlow.Tests.Services;

/// <summary>Plans and fair use: personal accounts stop at their minutes, an invoiced organisation never does.</summary>
public class BillingTests
{
    private sealed class Mailbox : IEmailSender
    {
        public string Name => "test-mail";
        public Task<string> SendAsync(EmailMessage message, CancellationToken ct) => Task.FromResult("ref");
    }

    /// <summary>The default plans, with the free personal plan and the trial cut to one minute so a test recording uses them up.</summary>
    private static Plans Small() => Plans.With(k => k is "Billing:Plans:free:MinutesPerMonth" or "Billing:Plans:org-trial:MinutesPerMonth" ? "1" : null);

    private static async Task<User> Personal(Env env, string name = "Pat Person") =>
        await new EntraUsers(env.Stores, env.Clock).ResolvePersonalAsync(new PersonalIdentity("google:" + name, name, "pat@gmail.test", "Google"));

    [Fact]
    public void There_are_four_organisation_tiers_and_three_paid_personal_ones_and_settings_change_them()
    {
        var plans = Plans.Default();
        Assert.Equal(["Trial", "Basic", "Business", "Professional", "Enterprise"], plans.For(true).Select(p => p.Name));
        Assert.Equal(["Free", "Basic", "Student", "Professional"], plans.For(false).Select(p => p.Name));
        Assert.All(plans.For(true).Where(p => !p.Free), p => Assert.True(p.InvoicesExtra));
        var changed = Plans.With(k => k switch
        {
            "Billing:Plans:basic:Price" => "£59 a month", "Billing:Plans:basic:MinutesPerMonth" => "9000",
            "Billing:Plans:personal-student:StripePriceId" => "price_123", _ => null,
        });
        Assert.Equal(("£59 a month", 9000), (changed.Find("basic")!.Price, changed.Find("basic")!.MinutesPerMonth));
        Assert.Equal("price_123", changed.Find("personal-student")!.StripePriceId);
    }

    [Fact]
    public async Task A_personal_account_stops_at_its_minutes_and_its_conversion_waits()
    {
        using var env = new Env();
        var billing = new BillingService(env.Stores, Small(), new StandInPayments(env.Clock), env.Clock);
        var processing = new ProcessingService(env.Stores, env.Speech, env.Ai, new StandInAudioEnhancer(), env.Clock, env.Reporter, billing: billing);
        var pat = await Personal(env);

        var first = await env.Meeting.CreateRecordingAsync(pat, env.Recording());
        await processing.ProcessAsync(first);
        Assert.Equal(1.0, (await billing.ViewAsync(pat)).MinutesUsed);

        var second = await env.Meeting.CreateRecordingAsync(pat, env.Recording("Second"));
        await processing.ProcessAsync(second);
        var item = await env.Item(second);
        Assert.Equal(ItemStatus.ConversionFailed, item.Status);
        Assert.Contains("You have used this month's 1 minutes", item.FailureReason);
        Assert.Contains(await env.Stores.Audit.ListAsync(second), a => a.Type == "processing.failed" && a.Details.Contains("plan-limit"));
        await Assert.ThrowsAsync<PlanLimitException>(() => billing.EnsureMinutesLeftAsync(pat));

        // The person is told, once, at 100%; the waiting recording says why.
        var n = new NotificationService(env.Stores, new Mailbox(), env.Clock, env.Reporter, "https://verbaflow.example");
        await n.ProcessAsync();
        env.Clock.Now = env.Clock.Now.AddSeconds(1);
        var third = await env.Meeting.CreateRecordingAsync(pat, env.Recording("Third"));
        await processing.ProcessAsync(third);
        await n.ProcessAsync();
        Assert.Contains((await n.ListAsync(pat)).Items, i => i.Text.Contains("is waiting: this month's minutes are used up"));

        // Next month the minutes are back and Retry works.
        env.Clock.Now = env.Clock.Now.AddMonths(1);
        await billing.EnsureMinutesLeftAsync(pat);
    }

    [Fact]
    public async Task Paying_on_the_pretend_page_starts_the_plan_and_cancelling_ends_it_at_the_month_end()
    {
        using var env = new Env();
        var pay = new StandInPayments(env.Clock);
        var billing = new BillingService(env.Stores, Small(), pay, env.Clock);
        var pat = await Personal(env);
        Assert.True((await billing.ViewAsync(pat)).CanBuy);
        await Assert.ThrowsAsync<ForbiddenException>(() => billing.ViewAsync(env.Alice)); // not an administrator
        await Assert.ThrowsAsync<DomainException>(() => billing.CheckoutAsync(pat, "basic", "http://x")); // an organisation plan

        var page = await billing.CheckoutAsync(pat, "personal-student", "http://x");
        Assert.Null(pay.Pay(env.Alice.Id, page.Reference)); // someone else's page
        await billing.ApplyAsync(pay.Pay(pat.Id, page.Reference)!);
        Assert.Null(pay.Pay(pat.Id, page.Reference)); // used once
        var view = await billing.ViewAsync(pat);
        Assert.Equal(("Student", true, false), (view.PlanName, view.CanManage, view.CanBuy));
        await billing.EnsureMinutesLeftAsync(pat);

        await billing.ApplyAsync(pay.Cancel(pat.Id, "end"));
        Assert.Equal("Student", (await billing.ViewAsync(pat)).PlanName); // kept to the end of the period
        env.Clock.Now = env.Clock.Now.AddMonths(1).AddDays(1);
        Assert.Equal("Free", (await billing.ViewAsync(pat)).PlanName);
        Assert.Contains(await env.Stores.Audit.ListAsync(), a => a.Type == "plan.changed");
    }

    [Fact]
    public async Task An_invoiced_organisation_is_never_refused_and_its_minutes_go_to_the_meter_once()
    {
        using var env = new Env();
        var meter = new StandInUsageMeter();
        var billing = new BillingService(env.Stores, Small(), new StandInPayments(env.Clock), env.Clock, meter: meter);
        var sam = T.U("Sam") with { IsSupport = true };

        // On the trial (one minute here) the organisation stops at its minutes.
        await billing.RecordAsync(env.Alice, "live", 60_000);
        await Assert.ThrowsAsync<PlanLimitException>(() => billing.EnsureMinutesLeftAsync(env.Bob));

        // Administrators cannot buy or change it; VerbaFlow support sets it.
        await Assert.ThrowsAsync<DomainException>(() => billing.CheckoutAsync(env.Carol, "basic", "http://x"));
        await Assert.ThrowsAsync<ForbiddenException>(() => billing.SetOrganisationPlanAsync(env.Carol, "basic", "cus_1"));
        await Assert.ThrowsAsync<DomainException>(() => billing.SetOrganisationPlanAsync(sam, "basic", "cus 1; drop"));
        var view = await billing.SetOrganisationPlanAsync(sam, "basic", "cus_ABC");
        Assert.Equal("Basic", view.PlanName);

        for (var i = 0; i < 3; i++) await billing.EnsureMinutesLeftAsync(env.Bob);
        await billing.RecordAsync(env.Bob, "recording", 90_500);
        await billing.RecordAsync(env.Alice, "widget", 1_200);
        Assert.Equal(1, await billing.SendUsageAsync());
        Assert.Equal(0, await billing.SendUsageAsync()); // nothing new (the 0.7 s left waits)
        var sent = Assert.Single(meter.Sent);
        Assert.Equal(("cus_ABC", 91L, "vf-2026-10-1"), (sent.Customer, sent.Seconds, sent.Key));

        // Minutes of a month that has ended land on that month's invoice.
        await billing.RecordAsync(env.Bob, "recording", 2_000);
        env.Clock.Now = new DateTimeOffset(2026, 11, 1, 0, 5, 0, TimeSpan.Zero);
        Assert.Equal(1, await billing.SendUsageAsync());
        Assert.Equal(new DateTimeOffset(2026, 10, 31, 23, 59, 59, TimeSpan.Zero), meter.Sent[^1].At);
        Assert.Equal("vf-2026-10-2", meter.Sent[^1].Key);

        var adminView = await billing.ViewAsync(env.Carol);
        Assert.True(adminView.Organisation);
        Assert.False(adminView.CanSetPlan);
        Assert.Null(adminView.CustomerId);
    }

    [Fact]
    public async Task Administrators_are_told_at_80_and_100_percent_of_the_organisations_minutes()
    {
        using var env = new Env();
        var billing = new BillingService(env.Stores, Plans.With(k => k == "Billing:Plans:org-trial:MinutesPerMonth" ? "10" : null), new StandInPayments(env.Clock), env.Clock);
        var n = new NotificationService(env.Stores, new Mailbox(), env.Clock, env.Reporter, "https://verbaflow.example");
        await n.ProcessAsync();
        await billing.RecordAsync(env.Alice, "live", 8 * 60_000);
        await billing.RecordAsync(env.Alice, "live", 60_000); // still 90%: no second warning
        await billing.RecordAsync(env.Bob, "live", 60_000);
        await n.ProcessAsync();
        var texts = (await n.ListAsync(env.Carol)).Items.Select(i => i.Text).ToList();
        Assert.Equal(["Your organisation has used all of this month's minutes. New conversions will wait until next month or a bigger plan.",
            "Your organisation has used 80% of this month's minutes."], texts);
        Assert.Empty((await n.ListAsync(env.Alice)).Items);
    }

    [Fact]
    public void Stripe_notices_are_trusted_only_with_a_fresh_signature_and_name_the_plan()
    {
        var plans = Plans.With(k => k == "Billing:Plans:personal-professional:StripePriceId" ? "price_pro" : null);
        var account = Guid.NewGuid();
        var body = "{\"type\":\"customer.subscription.updated\",\"data\":{\"object\":{\"id\":\"sub_1\",\"customer\":\"cus_1\",\"status\":\"active\","
            + "\"cancel_at_period_end\":true,\"metadata\":{\"account\":\"" + account + "\"},"
            + "\"items\":{\"data\":[{\"price\":{\"id\":\"price_pro\"},\"current_period_end\":1800000000}]}}}}";
        var now = DateTimeOffset.FromUnixTimeSeconds(1_790_000_000);
        string Sign(long t, string secret) => $"t={t},v1=" + Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes($"{t}.{body}"))).ToLowerInvariant();
        Assert.True(StripePayments.Verify(body, Sign(now.ToUnixTimeSeconds(), "whsec_test"), "whsec_test", now));
        Assert.False(StripePayments.Verify(body, Sign(now.ToUnixTimeSeconds(), "other"), "whsec_test", now));
        Assert.False(StripePayments.Verify(body, Sign(now.ToUnixTimeSeconds() - 600, "whsec_test"), "whsec_test", now)); // too old
        Assert.False(StripePayments.Verify(body + " ", Sign(now.ToUnixTimeSeconds(), "whsec_test"), "whsec_test", now));
        Assert.False(StripePayments.Verify(body, null, "whsec_test", now));

        var n = StripePayments.Parse(body, plans);
        Assert.Equal((account, "personal-professional", PlanStatus.Active, true, "sub_1"), (n.AccountId!.Value, n.PlanId!, n.Status!.Value, n.CancelAtPeriodEnd!.Value, n.SubscriptionId!));
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1_800_000_000), n.PeriodEnd);
        Assert.StartsWith("ignored", StripePayments.Parse("""{"type":"invoice.paid","data":{"object":{}}}""", plans).Kind);
    }

    [Fact]
    public async Task With_real_Stripe_a_plan_without_its_price_cannot_be_bought()
    {
        using var env = new Env();
        var stripe = new StripePayments(new HttpClient(), new StripeOptions("sk_test_x", "whsec_x"), Plans.Default(), env.Clock);
        var billing = new BillingService(env.Stores, Plans.Default(), stripe, env.Clock);
        var pat = await Personal(env);
        var e = await Assert.ThrowsAsync<DomainException>(() => billing.CheckoutAsync(pat, "personal-basic", "http://x"));
        Assert.Equal("The Basic plan cannot be bought yet. Please try again later.", e.Message);
    }

    [Fact]
    public async Task Live_speech_counts_what_was_heard_and_is_refused_once_the_minutes_are_used()
    {
        using var env = new Env();
        var billing = new BillingService(env.Stores, Small(), new StandInPayments(env.Clock), env.Clock);
        var sessions = new LiveSessions(env.Stores, new StandInLiveSpeech(), env.Clock, billing);
        var pat = await Personal(env);
        var (s, _) = await sessions.StartAsync(pat, "en-GB");
        var (gen, _) = s.Connect(0);
        s.Heard(gen, 32 * 45_000); // 45 seconds
        sessions.Discard(pat, s.Id);
        await sessions.LastCount;
        Assert.Equal(0.8, (await billing.ViewAsync(pat)).MinutesUsed);
        Assert.Equal("Speech mode (live text)", Assert.Single((await billing.ViewAsync(pat)).Recent).What);

        var (s2, _) = await sessions.StartAsync(pat, "en-GB"); // 15 s left: still allowed to start
        var (g2, _) = s2.Connect(0);
        s2.Heard(g2, 32 * 20_000); // speech already under way is not cut off
        sessions.Discard(pat, s2.Id);
        await sessions.LastCount;
        await Assert.ThrowsAsync<PlanLimitException>(() => sessions.StartAsync(pat, "en-GB"));
    }
}
