using VerbaFlow.Core.Audit;
using VerbaFlow.Core.Domain;
using VerbaFlow.Core.Providers;

namespace VerbaFlow.Infrastructure.Services;

public sealed record PlanOption(string Id, string Name, int MinutesPerMonth, string Price, int? People, string? Extra, bool Current);

/// <summary>The organisation's subscription when it is set in settings (Billing:OrganisationPlan, Billing:OrganisationCustomerId).</summary>
public sealed record OrganisationPlanSetting(string? PlanId, string? CustomerId);
public sealed record UsageRow(DateTimeOffset At, string What, string? Who, int Seconds, Guid? ItemId);

/// <summary>Everything the plan page shows.</summary>
public sealed record PlanView(bool Organisation, string PlanId, string PlanName, string Price, int MinutesPerMonth, int? People, string? Extra,
    double MinutesUsed, double MinutesLeft, double ExtraMinutes, string? ExtraCharge, DateTimeOffset ResetsOn, string Status, string? Warning,
    bool CanBuy, bool CanManage, bool CanSetPlan, string? CustomerId, bool SetInSettings, bool Pretend,
    IReadOnlyList<PlanOption> Plans, IReadOnlyList<UsageRow> Recent, int? PeopleThisMonth);

/// <summary>
/// Plans and fair use. Every use of speech turned into text (a conversion, Speech mode, the Widget) counts against an account: a
/// personal account's own, or the organisation's, which all its people share.
/// Personal accounts pay by card; when their minutes are used up, new conversions and new live speech wait until the next month or
/// a bigger plan (speech already under way is never cut off). The organisation is invoiced monthly for its subscription: its minutes
/// are never refused, and those over the plan are passed to the payment service for the next invoice (<see cref="SendUsageAsync"/>).
/// Its subscription is set by VerbaFlow (support users, or settings), not bought by its administrators.
/// The account's people are told at 80% and 100% (administrators, for the organisation's).
/// </summary>
public sealed class BillingService(Stores stores, Plans plans, IPayments payments, TimeProvider clock, bool enabled = true,
    IUsageMeter? meter = null, OrganisationPlanSetting? orgSetting = null)
{
    private readonly SemaphoreSlim gate = new(1, 1);
    public static readonly int[] WarnAt = [80, 100];
    public bool Enabled => enabled;
    public Plans Plans => plans;
    public IPayments Payments => payments;

    /// <summary>Whether the account is an organisation's (the first one's, or one VerbaFlow support added) rather than a personal one.</summary>
    private async Task<bool> IsOrgAsync(Guid account) => account == Organisations.Default || await stores.Organisations.GetAsync(account) is not null;

    private static string Minutes(int m) => m % 60 == 0 && m >= 60 ? $"{m / 60:N0} hour{(m == 60 ? "" : "s")}" : $"{m} minutes";

    private DateTimeOffset ResetDate()
    {
        var now = clock.GetUtcNow().UtcDateTime;
        return new DateTimeOffset(new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc).AddMonths(1));
    }

    /// <summary>The sentence shown when an account has used its minutes (the same everywhere).</summary>
    public string LimitSentence(Plan plan, bool organisation) => organisation
        ? $"Your organisation has used this month's {Minutes(plan.MinutesPerMonth)} on its {plan.Name} plan. An administrator can ask VerbaFlow for a bigger plan, or wait until {ResetDate():d MMMM}."
        : $"You have used this month's {Minutes(plan.MinutesPerMonth)} on your {plan.Name} plan. Choose a bigger plan on Your plan (More ▾), or wait until {ResetDate():d MMMM}.";

    /// <summary>The plan an account is on now: a paid plan that was cancelled and has run out counts as the free one.</summary>
    public async Task<(Plan Plan, PlanRecord? Record)> PlanOfAsync(Guid account)
    {
        var isOrg = await IsOrgAsync(account);
        if (account == Organisations.Default && orgSetting?.PlanId is { Length: > 0 } fixedPlan)
            return (plans.Find(fixedPlan) is { ForOrganisation: true } p ? p : plans.FreeFor(true),
                new PlanRecord(account, fixedPlan, PlanStatus.Active, "settings", orgSetting.CustomerId, null, null, false, clock.GetUtcNow()));
        var r = await stores.Plans.GetAsync(account);
        if (r is { CancelAtPeriodEnd: true, PeriodEnd: { } end } && end <= clock.GetUtcNow()) return (plans.FreeFor(isOrg), r);
        return (plans.Of(r, isOrg), r);
    }

    public async Task<long> UsedMsAsync(Guid account) =>
        (await stores.Usage.GetAsync(UsageMonth.IdOf(account, UsageMonth.MonthOf(clock.GetUtcNow()))))?.Ms ?? 0;

    /// <summary>Refuses (PlanLimitException with a plain sentence) when the account has no minutes left this month.</summary>
    public async Task EnsureMinutesLeftAsync(Guid account, Guid? userId)
    {
        if (!enabled) return;
        var (plan, _) = await PlanOfAsync(account);
        if (plan.InvoicesExtra) return; // an invoiced organisation is never refused: extra minutes go on the next invoice
        var used = await UsedMsAsync(account);
        if (used < plan.IncludedMs) return;
        await stores.Audit.AppendAsync(userId, userId is null ? "system" : "user", "platform", null, "plan.limit_reached",
            AuditChain.Details(("account", await IsOrgAsync(account) ? "organisation" : "personal"), ("plan", plan.Id), ("minutesUsed", (int)(used / 60_000))));
        throw new PlanLimitException(LimitSentence(plan, await IsOrgAsync(account)));
    }

    public Task EnsureMinutesLeftAsync(User user) => EnsureMinutesLeftAsync(BillingAccounts.Of(user), user.Id);
    public Task EnsureMinutesLeftAsync(Item item) => EnsureMinutesLeftAsync(BillingAccounts.Of(item), null);

    /// <summary>Counts speech turned into text: kind "recording" (a conversion), "live" (Speech mode) or "widget".</summary>
    public async Task RecordAsync(Guid account, Guid? userId, string kind, int ms, Guid? itemId = null)
    {
        if (!enabled || ms <= 0) return;
        var now = clock.GetUtcNow();
        var month = UsageMonth.MonthOf(now);
        var id = UsageMonth.IdOf(account, month);
        int? crossed = null; Plan plan;
        await gate.WaitAsync();
        try
        {
            PlanRecord? record;
            (plan, record) = await PlanOfAsync(account);
            var m = await stores.Usage.GetAsync(id) ?? new UsageMonth(id, account, month, 0, []);
            var next = m.Add(new UsageEntry(now, kind, ms, itemId, userId));
            // An invoiced organisation's minutes are passed to the payment service (all of them: its price makes the included ones free).
            if (plan.InvoicesExtra && record?.CustomerId is { Length: > 0 }) next = next with { UnsentMs = next.UnsentMs + ms };
            var pct = plan.MinutesPerMonth <= 0 ? 100 : next.Ms * 100.0 / (plan.MinutesPerMonth * 60_000.0);
            var warned = m.Warned ?? [];
            crossed = WarnAt.Where(w => pct >= w && !warned.Contains(w)).Select(w => (int?)w).LastOrDefault();
            if (crossed is { } c) next = next with { Warned = [.. warned, .. WarnAt.Where(w => w <= c && !warned.Contains(w))] };
            await stores.Usage.UpsertAsync(id, next);
        }
        finally { gate.Release(); }
        if (crossed is { } p)
            await stores.Audit.AppendAsync(null, "system", "platform", null, "plan.usage_warning",
                AuditChain.Details(("account", await IsOrgAsync(account) ? "organisation" : account.ToString()), ("organisation", await IsOrgAsync(account) ? account : null),
                    ("percent", p), ("plan", plan.Id)));
    }

    public Task RecordAsync(User user, string kind, int ms, Guid? itemId = null) => RecordAsync(BillingAccounts.Of(user), user.Id, kind, ms, itemId);
    public Task RecordAsync(Item item, string kind, int ms) => RecordAsync(BillingAccounts.Of(item), item.OwnerId, kind, ms, item.Id);

    /// <summary>The account a person may see and manage: their own personal one, or the organisation's for its administrators.</summary>
    private static Guid ManagedBy(User user, Guid? organisation = null)
    {
        if (user.Individual) return user.Id;
        if (user.IsSupport) return organisation ?? Organisations.Default;
        if (user.IsAdmin) return user.Org!.Value;
        throw new ForbiddenException("Only administrators can see the organisation's plan.");
    }

    private static string? ExtraText(Plan p) => p.ExtraPerHour is { } x ? $"£{x:0.00} for each extra hour" : null;

    public async Task<PlanView> ViewAsync(User user, Guid? organisation = null)
    {
        var account = ManagedBy(user, organisation);
        var org = await IsOrgAsync(account);
        if (!org && account != user.Id) throw new NotFoundException("That organisation does not exist.");
        var fixedInSettings = account == Organisations.Default && orgSetting?.PlanId is { Length: > 0 };
        var (plan, r) = await PlanOfAsync(account);
        var usage = await stores.Usage.GetAsync(UsageMonth.IdOf(account, UsageMonth.MonthOf(clock.GetUtcNow())));
        var usedMin = Math.Round((usage?.Ms ?? 0) / 60_000.0, 1);
        var paid = !plan.Free && r is not null;
        var status = paid ? r!.Status switch
        {
            PlanStatus.PastDue => "The last payment did not go through. Update the card to keep the plan.",
            _ when r.CancelAtPeriodEnd => $"Cancelled: the plan ends on {r.PeriodEnd:d MMMM yyyy}.",
            _ => r.PeriodEnd is { } e ? $"Renews on {e:d MMMM yyyy}." : "Active.",
        } : org ? "Trial: no invoice. When its minutes are used up, new conversions wait until next month." : "Free plan.";
        if (org && !plan.Free) status = $"Invoiced monthly{(r?.CustomerId is { Length: > 0 } ? "" : " (not yet linked to an invoice: VerbaFlow support must add the customer reference)")}. To change the plan, contact VerbaFlow.";
        var pct = plan.MinutesPerMonth <= 0 ? 100 : usedMin * 100 / plan.MinutesPerMonth;
        var extraMin = plan.InvoicesExtra ? Math.Max(0, Math.Round(usedMin - plan.MinutesPerMonth, 1)) : 0;
        string? extraCharge = plan.InvoicesExtra && plan.ExtraPerHour is { } rate && extraMin > 0 ? $"about £{rate * (decimal)extraMin / 60m:0.00}" : null;
        string? warning = plan.InvoicesExtra
            ? pct >= 100 ? $"This month's included minutes are used. Extra minutes ({ExtraText(plan)}) will be on the next invoice." 
              : pct >= 80 ? $"{pct:N0}% of this month's included minutes are used. After that, extra minutes go on the next invoice." : null
            : (usage?.Ms ?? 0) >= plan.IncludedMs ? LimitSentence(plan, org)
            : pct >= 80 ? $"{pct:N0}% of this month's minutes are used." : null;
        var entries = (usage?.Entries ?? []).Take(30).ToList();
        var names = new Dictionary<Guid, string>(); var people = new Dictionary<Guid, string>();
        foreach (var e in entries)
        {
            if (e.ItemId is { } id && !names.ContainsKey(id)) names[id] = (await stores.Items.GetAsync(id))?.Name ?? "";
            if (org && e.UserId is { } u && !people.ContainsKey(u)) people[u] = (await stores.Users.GetAsync(u))?.Name ?? "";
        }
        string What(UsageEntry e) => e.Kind switch
        {
            "recording" => e.ItemId is { } id && names.GetValueOrDefault(id) is { Length: > 0 } n ? $"Converted: {n}" : "A recording converted",
            "live" => "Speech mode (live text)",
            "widget" => "Widget",
            _ => e.Kind,
        };
        int? peopleThisMonth = org ? (usage?.Entries ?? []).Select(e => e.UserId).Where(u => u is not null).Distinct().Count() : null;
        return new PlanView(org, plan.Id, plan.Name, plan.Price, plan.MinutesPerMonth, plan.People, ExtraText(plan),
            usedMin, Math.Max(0, Math.Round(plan.MinutesPerMonth - usedMin, 1)), extraMin, extraCharge, ResetDate(), status, warning,
            !org && plan.Free, !org && paid && r!.CustomerId is not null, org && user.IsSupport && !fixedInSettings,
            org && user.IsSupport ? r?.CustomerId : null, org && fixedInSettings, org ? meter?.Pretend ?? true : payments.Pretend,
            [.. plans.For(org).Select(p => new PlanOption(p.Id, p.Name, p.MinutesPerMonth, p.Price, p.People, ExtraText(p), p.Id == plan.Id))],
            [.. entries.Select(e => new UsageRow(e.At, What(e), org && e.UserId is { } u ? people.GetValueOrDefault(u) : null, (int)Math.Round(e.Ms / 1000.0), e.ItemId))],
            peopleThisMonth);
    }

    public async Task<PaymentPage> CheckoutAsync(User user, string? planId, string origin, CancellationToken ct = default)
    {
        var account = ManagedBy(user);
        if (await IsOrgAsync(account)) throw new DomainException("The organisation's plan is invoiced monthly. To change it, contact VerbaFlow.");
        var plan = plans.Find(planId);
        if (plan is null || plan.ForOrganisation) throw new DomainException("Choose a plan.");
        if (plan.Free) throw new DomainException("This plan needs no payment. To leave a paid plan, use Manage or cancel.");
        if (!payments.Pretend && (plan.StripePriceId is not { Length: > 0 } price || price.StartsWith("stand-in-")))
            throw new DomainException($"The {plan.Name} plan cannot be bought yet. Please try again later.");
        var (current, _) = await PlanOfAsync(account);
        if (!current.Free) throw new DomainException("There is already a paid plan. To change it, use Manage the plan.");
        var page = await payments.CheckoutAsync(user, account, plan, origin + "/#/plan", ct);
        await stores.Audit.AppendAsync(user.Id, "user", "platform", null, "plan.checkout_started",
            AuditChain.Details(("account", "personal"), ("plan", plan.Id), ("paymentService", payments.Name), ("reference", page.Reference)));
        return page;
    }

    public async Task<PaymentPage> ManageAsync(User user, string origin, CancellationToken ct = default)
    {
        var account = ManagedBy(user);
        if (await IsOrgAsync(account)) throw new DomainException("The organisation's plan is invoiced monthly. To change it, contact VerbaFlow.");
        var (plan, r) = await PlanOfAsync(account);
        if (plan.Free || r?.CustomerId is null) throw new DomainException("There is no paid plan, so there is nothing to manage.");
        return await payments.ManageAsync(r, origin + "/#/plan", ct);
    }

    /// <summary>A notice from the payment service: starts, renews, changes or ends a plan. False when it could not be trusted.</summary>
    public async Task<bool> HandleNoticeAsync(string body, string? signature)
    {
        var n = payments.Read(body, signature);
        if (n is null) return false;
        if (!n.Kind.StartsWith("ignored")) await ApplyAsync(n);
        return true;
    }

    /// <summary>The account a person who started the stand-in's pretend pages pays for (their own, or the organisation's).</summary>
    public static Guid AccountFor(User user) => ManagedBy(user);

    /// <summary>
    /// VerbaFlow support sets the organisation's subscription: its plan and the payment service's customer it is invoiced as.
    /// Not when settings already name it (settings win).
    /// </summary>
    public async Task<PlanView> SetOrganisationPlanAsync(User user, string? planId, string? customerId, Guid? organisation = null)
    {
        if (!user.IsSupport) throw new ForbiddenException("Only VerbaFlow support can change the organisation's plan.");
        var account = organisation ?? Organisations.Default;
        if (!await IsOrgAsync(account)) throw new NotFoundException("That organisation does not exist.");
        if (account == Organisations.Default && orgSetting?.PlanId is { Length: > 0 }) throw new DomainException("The organisation's plan is set in VerbaFlow's settings, so it cannot be changed here.");
        if (plans.Find(planId) is not { ForOrganisation: true } plan) throw new DomainException("Choose one of the organisation plans.");
        var customer = string.IsNullOrWhiteSpace(customerId) ? null : customerId.Trim();
        if (customer is not null && (customer.Length > 100 || !System.Text.RegularExpressions.Regex.IsMatch(customer, "^[A-Za-z0-9_-]+$")))
            throw new DomainException("The customer reference is letters, numbers, _ and - only (for example cus_ABC123).");
        await gate.WaitAsync();
        try
        {
            var next = new PlanRecord(account, plan.Id, PlanStatus.Active, "invoice", plan.Free ? null : customer, null, null, false, clock.GetUtcNow());
            await stores.Plans.UpsertAsync(account, next);
        }
        finally { gate.Release(); }
        await stores.Audit.AppendAsync(user.Id, "support", "platform", null, "plan.changed",
            AuditChain.Details(("account", "organisation"), ("organisation", account), ("plan", plan.Id), ("status", "Active"), ("paymentService", "invoice"), ("customerLinked", customer is not null && !plan.Free)));
        return await ViewAsync(user, account);
    }

    /// <summary>
    /// Passes the organisation's minutes not yet sent to the payment service (run every few minutes). Each sending has its own key
    /// (month and number), so a sending that is repeated after a failure is counted once. Minutes of a month that has ended are dated
    /// at its last second, so they land on that month's invoice. Whole seconds are sent; the rest waits for the next sending.
    /// </summary>
    public async Task<int> SendUsageAsync(CancellationToken ct = default)
    {
        if (!enabled || meter is null) return 0;
        var sent = 0;
        foreach (var account in (IReadOnlyList<Guid>)[Organisations.Default, .. (await stores.Organisations.ListAsync()).Select(o => o.Id)])
            sent += await SendUsageAsync(account, ct);
        return sent;
    }

    private async Task<int> SendUsageAsync(Guid account, CancellationToken ct)
    {
        var (_, record) = await PlanOfAsync(account);
        if (record?.CustomerId is not { Length: > 0 } customer) return 0;
        var now = clock.GetUtcNow();
        var sent = 0;
        foreach (var month in new[] { UsageMonth.MonthOf(now.AddMonths(-1)), UsageMonth.MonthOf(now) })
        {
            var id = UsageMonth.IdOf(account, month);
            var m = await stores.Usage.GetAsync(id);
            if (m is null || m.UnsentMs < 1000) continue;
            var seconds = m.UnsentMs / 1000;
            var number = m.Sendings + 1;
            var at = month == UsageMonth.MonthOf(now) ? now
                : new DateTimeOffset(DateTime.ParseExact(month + "-01", "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture), TimeSpan.Zero).AddMonths(1).AddSeconds(-1);
            var key = account == Organisations.Default ? $"vf-{month}-{number}" : $"vf-{account:N}-{month}-{number}";
            await meter.SendAsync(customer, seconds, at, key, ct);
            await gate.WaitAsync(ct);
            try
            {
                var fresh = await stores.Usage.GetAsync(id) ?? m;
                await stores.Usage.UpsertAsync(id, fresh with { UnsentMs = fresh.UnsentMs - seconds * 1000, SentMs = fresh.SentMs + seconds * 1000, Sendings = number });
            }
            finally { gate.Release(); }
            sent++;
        }
        return sent;
    }

    public async Task ApplyAsync(PaymentNotice n)
    {
        await gate.WaitAsync();
        try
        {
            PlanRecord? r = null;
            if (n.AccountId is { } aid) r = await stores.Plans.GetAsync(aid);
            else if (n.SubscriptionId is not null || n.CustomerId is not null)
                r = (await stores.Plans.ListAsync()).FirstOrDefault(p => (n.SubscriptionId != null && p.SubscriptionId == n.SubscriptionId)
                    || (n.SubscriptionId == null && p.CustomerId == n.CustomerId));
            var accountId = n.AccountId ?? r?.AccountId;
            if (accountId is not { } id || await IsOrgAsync(id)) return; // not one of ours (the organisation is invoiced, not bought by card)
            var org = false;
            if (await stores.Users.GetAsync(id) is not { Individual: true }) return;
            var planId = n.PlanId ?? r?.PlanId;
            if (plans.Find(planId) is not { } found || found.ForOrganisation != org) planId = r?.PlanId ?? plans.FreeFor(org).Id;
            var next = new PlanRecord(id, planId!, n.Status ?? r?.Status ?? PlanStatus.Active, payments.Name,
                n.CustomerId ?? r?.CustomerId, n.SubscriptionId ?? r?.SubscriptionId, n.PeriodEnd ?? r?.PeriodEnd,
                n.CancelAtPeriodEnd ?? r?.CancelAtPeriodEnd ?? false, clock.GetUtcNow());
            if (r is not null && r with { UpdatedAt = next.UpdatedAt } == next) return; // nothing changed (the service repeats itself)
            await stores.Plans.UpsertAsync(id, next);
            await stores.Audit.AppendAsync(null, "system", "platform", null, "plan.changed",
                AuditChain.Details(("account", org ? "organisation" : id.ToString()), ("plan", next.PlanId), ("status", next.Status.ToString()),
                    ("cancelAtPeriodEnd", next.CancelAtPeriodEnd), ("periodEnd", next.PeriodEnd?.ToString("yyyy-MM-dd")), ("paymentService", payments.Name), ("notice", n.Kind)));
        }
        finally { gate.Release(); }
    }
}
