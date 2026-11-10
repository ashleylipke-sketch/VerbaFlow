using VerbaFlow.Core.Audit;
using VerbaFlow.Core.Domain;
using VerbaFlow.Core.Providers;

namespace VerbaFlow.Infrastructure.Services;

/// <param name="Switch">For a personal account on a paid plan: "now" (a bigger plan starts at once) or "renewal" (a smaller one starts when the month paid for ends).</param>
public sealed record PlanOption(string Id, string Name, int MinutesPerMonth, string Price, int? People, string? Extra, bool Current,
    string? Switch = null, bool StudentCheckNeeded = false, bool Next = false);

/// <summary>A personal account's student check: the school's domain, until when it holds, and whether it is time to check again.</summary>
public sealed record StudentView(string Domain, DateTimeOffset Until, bool AskAgain, bool RunOut);

/// <summary>The organisation's subscription when it is set in settings (Billing:OrganisationPlan, Billing:OrganisationCustomerId).</summary>
public sealed record OrganisationPlanSetting(string? PlanId, string? CustomerId);
public sealed record UsageRow(DateTimeOffset At, string What, string? Who, int Seconds, Guid? ItemId);

/// <summary>Everything the plan page shows.</summary>
public sealed record PlanView(bool Organisation, string PlanId, string PlanName, string Price, int MinutesPerMonth, int? People, string? Extra,
    double MinutesUsed, double MinutesLeft, double ExtraMinutes, string? ExtraCharge, DateTimeOffset ResetsOn, string Status, string? Warning,
    bool CanBuy, bool CanManage, bool CanSetPlan, string? CustomerId, bool SetInSettings, bool Pretend,
    IReadOnlyList<PlanOption> Plans, IReadOnlyList<UsageRow> Recent, int? PeopleThisMonth,
    bool CanChange = false, string? NextPlanName = null, DateTimeOffset? NextPlanFrom = null, StudentView? Student = null);

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
    IUsageMeter? meter = null, OrganisationPlanSetting? orgSetting = null, IReadOnlyList<string>? studentDomains = null)
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
        r = Settle(r);
        return (plans.Of(r, isOrg), r);
    }

    /// <summary>A smaller plan the person switched to starts once the month they paid for has ended: the record as it stands now.</summary>
    private PlanRecord? Settle(PlanRecord? r) =>
        r is { NextPlanId: { } next, NextPlanFrom: { } from } && from <= clock.GetUtcNow()
            ? r with { PlanId = plans.Find(next) is { ForOrganisation: false, Free: false } ? next : r.PlanId, NextPlanId = null, NextPlanFrom = null }
            : r;

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
        var canChange = !org && CanChange(plan, r);
        var next = !org && paid ? PendingNext(plan, r) : null;
        var student = org ? null : await StudentViewAsync(user.Id);
        var studentOk = student is { RunOut: false };
        if (!org && plan.Id == Plans.StudentId && paid && student is { AskAgain: true } sv)
            warning ??= sv.RunOut
                ? $"Your student check has run out. Check your student email again to keep the Student price; otherwise the plan moves to Basic when the month ends."
                : $"Your student check runs out on {sv.Until:d MMMM yyyy}. Check your student email again to keep the Student price.";
        return new PlanView(org, plan.Id, plan.Name, plan.Price, plan.MinutesPerMonth, plan.People, ExtraText(plan),
            usedMin, Math.Max(0, Math.Round(plan.MinutesPerMonth - usedMin, 1)), extraMin, extraCharge, ResetDate(), status, warning,
            !org && plan.Free, !org && paid && r!.CustomerId is not null, org && user.IsSupport && !fixedInSettings,
            org && user.IsSupport ? r?.CustomerId : null, org && fixedInSettings, org ? meter?.Pretend ?? true : payments.Pretend,
            [.. plans.For(org).Select(p => new PlanOption(p.Id, p.Name, p.MinutesPerMonth, p.Price, p.People, ExtraText(p), p.Id == plan.Id,
                canChange && !p.Free && p.Id != plan.Id ? (Bigger(p, plan) ? "now" : "renewal") : null,
                !org && p.Id == Plans.StudentId && !studentOk, next?.Id == p.Id))],
            [.. entries.Select(e => new UsageRow(e.At, What(e), org && e.UserId is { } u ? people.GetValueOrDefault(u) : null, (int)Math.Round(e.Ms / 1000.0), e.ItemId))],
            peopleThisMonth, canChange, next?.Name, next is null ? null : r!.NextPlanFrom, student);
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
        if (!current.Free) throw new DomainException("There is already a paid plan. Use Switch on the plan you want instead.");
        if (plan.Id == Plans.StudentId && !await IsStudentAsync(user.Id))
            throw new DomainException("The Student plan needs a student email address. Check yours first.");
        var page = await payments.CheckoutAsync(user, account, plan, origin + "/#/plan", ct);
        await stores.Audit.AppendAsync(user.Id, "user", "platform", null, "plan.checkout_started",
            AuditChain.Details(("account", "personal"), ("plan", plan.Id), ("paymentService", payments.Name), ("reference", page.Reference)));
        return page;
    }

    /// <summary>A bigger plan (more minutes) starts at once; the same or fewer minutes wait for the end of the month paid for.</summary>
    private static bool Bigger(Plan to, Plan from) => to.MinutesPerMonth > from.MinutesPerMonth;

    /// <summary>A paid personal plan that is not being cancelled, held with the payment service, can move to another paid plan.</summary>
    private static bool CanChange(Plan plan, PlanRecord? r) =>
        !plan.Free && r is { Status: PlanStatus.Active or PlanStatus.PastDue, CancelAtPeriodEnd: false, SubscriptionId: not null };

    /// <summary>The smaller plan waiting to start, when there is one.</summary>
    private Plan? PendingNext(Plan current, PlanRecord? r) =>
        r is { NextPlanId: { } n, NextPlanFrom: { } from } && from > clock.GetUtcNow() && n != current.Id && plans.Find(n) is { ForOrganisation: false, Free: false } p ? p : null;

    /// <summary>
    /// Moves a personal account's paid plan to another paid plan, on the same card. A bigger plan starts at once and the difference for
    /// the rest of the month is charged now; a smaller one starts when the month already paid for ends (nothing refunded, the minutes
    /// paid for stay until then). Choosing the plan one is on takes back a switch that has not started yet.
    /// </summary>
    public async Task<PlanView> ChangePlanAsync(User user, string? planId, CancellationToken ct = default)
    {
        var account = ManagedBy(user);
        if (await IsOrgAsync(account)) throw new DomainException("The organisation's plan is invoiced monthly. To change it, contact VerbaFlow.");
        if (plans.Find(planId) is not { ForOrganisation: false } to) throw new DomainException("Choose a plan.");
        var (current, r) = await PlanOfAsync(account);
        if (current.Free || r is null) throw new DomainException("There is no paid plan yet. Choose a plan to start one.");
        if (to.Free) throw new DomainException("To go back to the Free plan, cancel under Manage the plan. The plan you paid for carries on until the month ends.");
        if (r.CancelAtPeriodEnd) throw new DomainException("The plan is cancelled. To switch to another plan, first keep the plan under Manage the plan.");
        if (!CanChange(current, r)) throw new DomainException("The plan cannot be switched at the moment. Please try again later.");
        if (!payments.Pretend && (to.StripePriceId is not { Length: > 0 } price || price.StartsWith("stand-in-")))
            throw new DomainException($"The {to.Name} plan cannot be bought yet. Please try again later.");
        var pending = PendingNext(current, r);
        if (to.Id == current.Id)
        {
            if (pending is null) throw new DomainException($"You are already on the {to.Name} plan.");
            // Take back the switch: the next renewal stays at this plan's price.
            await payments.ChangeAsync(r, current, false, ct);
            await SetNextAsync(account, null, null);
            await AuditChangeAsync(user, current, to, "kept");
            return await ViewAsync(user);
        }
        if (to.Id == Plans.StudentId && !await IsStudentAsync(user.Id))
            throw new DomainException("The Student plan needs a student email address. Check yours first.");
        if (Bigger(to, current))
        {
            var notice = await payments.ChangeAsync(r, to, true, ct);
            await SetNextAsync(account, null, null);
            if (notice is not null) await ApplyAsync(notice);
            await AuditChangeAsync(user, current, to, "now");
        }
        else
        {
            await payments.ChangeAsync(r, to, false, ct);
            var from = r.PeriodEnd is { } e && e > clock.GetUtcNow() ? e : clock.GetUtcNow();
            await SetNextAsync(account, to.Id, from);
            await AuditChangeAsync(user, current, to, "at renewal");
        }
        return await ViewAsync(user);
    }

    private async Task SetNextAsync(Guid account, string? next, DateTimeOffset? from)
    {
        await gate.WaitAsync();
        try
        {
            if (Settle(await stores.Plans.GetAsync(account)) is { } fresh)
                await stores.Plans.UpsertAsync(account, fresh with { NextPlanId = next, NextPlanFrom = from, UpdatedAt = clock.GetUtcNow() });
        }
        finally { gate.Release(); }
    }

    private Task AuditChangeAsync(User user, Plan from, Plan to, string when) =>
        stores.Audit.AppendAsync(user.Id, "user", "platform", null, "plan.switch_requested",
            AuditChain.Details(("account", "personal"), ("from", from.Id), ("to", to.Id), ("starts", when), ("paymentService", payments.Name)));

    /// <summary>Whether an address counts as a school, college or university one (including the domains added in settings).</summary>
    public bool IsStudentAddress(string email) => StudentEmails.IsStudentAddress(email, studentDomains);

    /// <summary>The person's student check, if it has not run out.</summary>
    public async Task<bool> IsStudentAsync(Guid userId) => await stores.StudentChecks.GetAsync(userId) is { } c && c.Until > clock.GetUtcNow();

    private async Task<StudentView?> StudentViewAsync(Guid userId) => await stores.StudentChecks.GetAsync(userId) is { } c
        ? new StudentView(c.Domain, c.Until, c.Until - clock.GetUtcNow() <= StudentEmails.AskAgainBefore, c.Until <= clock.GetUtcNow())
        : null;

    /// <summary>
    /// The person typed back the code sent to their student address: they count as a student for a year. One address serves one
    /// account. A Student plan that was about to move to Basic because the check had run out stays Student.
    /// </summary>
    public async Task<PlanView> RecordStudentAsync(User user, string email, CancellationToken ct = default)
    {
        if (!user.Individual) throw new ForbiddenException("Only a personal account can choose the Student plan.");
        if (!IsStudentAddress(email)) throw new DomainException("That address does not look like a school, college or university address.");
        var hash = StudentEmails.Hash(email);
        var now = clock.GetUtcNow();
        if ((await stores.StudentChecks.ListAsync()).Any(c => c.AddressHash == hash && c.UserId != user.Id && c.Until > now))
            throw new DomainException("That student address is already used for another VerbaFlow account.");
        var check = new StudentCheck(user.Id, StudentEmails.DomainOf(email), hash, now, now + StudentEmails.Valid);
        await stores.StudentChecks.UpsertAsync(user.Id, check);
        await stores.Audit.AppendAsync(user.Id, "user", "platform", null, "plan.student_checked",
            AuditChain.Details(("domain", check.Domain), ("until", check.Until.ToString("yyyy-MM-dd"))));
        var (plan, r) = await PlanOfAsync(user.Id);
        if (plan.Id == Plans.StudentId && r is { NextPlanId: Plans.PersonalBasicId } && PendingNext(plan, r) is not null)
        {
            await payments.ChangeAsync(r, plan, false, ct);
            await SetNextAsync(user.Id, null, null);
            await stores.Audit.AppendAsync(user.Id, "user", "platform", null, "plan.switch_requested",
                AuditChain.Details(("account", "personal"), ("from", plan.Id), ("to", plan.Id), ("starts", "kept (student checked again)"), ("paymentService", payments.Name)));
        }
        return await ViewAsync(user);
    }

    /// <summary>
    /// Student plans whose student check has run out move to Basic when the month paid for ends (run every few minutes; the
    /// person is told on the plan page from 30 days before and can check again to stay on Student).
    /// </summary>
    public async Task<int> StudentRenewalsAsync(CancellationToken ct = default)
    {
        if (!enabled || plans.Find(Plans.PersonalBasicId) is not { } basic) return 0;
        var moved = 0;
        if (!payments.Pretend && (basic.StripePriceId is not { Length: > 0 } bp || bp.StartsWith("stand-in-"))) return 0;
        foreach (var r in (await stores.Plans.ListAsync()).Select(Settle).OfType<PlanRecord>())
        {
            if (r.PlanId != Plans.StudentId || r.NextPlanId is not null || !CanChange(plans.Find(r.PlanId)!, r)) continue;
            if (await IsStudentAsync(r.AccountId) || await stores.Users.GetAsync(r.AccountId) is not { Individual: true }) continue;
            await payments.ChangeAsync(r, basic, false, ct);
            var from = r.PeriodEnd is { } e && e > clock.GetUtcNow() ? e : clock.GetUtcNow();
            await SetNextAsync(r.AccountId, basic.Id, from);
            await stores.Audit.AppendAsync(null, "system", "platform", null, "plan.student_check_expired",
                AuditChain.Details(("account", r.AccountId.ToString()), ("from", r.PlanId), ("to", basic.Id), ("starts", from.ToString("yyyy-MM-dd"))));
            moved++;
        }
        return moved;
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
            r = Settle(r);
            var accountId = n.AccountId ?? r?.AccountId;
            if (accountId is not { } id || await IsOrgAsync(id)) return; // not one of ours (the organisation is invoiced, not bought by card)
            var org = false;
            if (await stores.Users.GetAsync(id) is not { Individual: true }) return;
            var planId = n.PlanId ?? r?.PlanId;
            if (plans.Find(planId) is not { } found || found.ForOrganisation != org) planId = r?.PlanId ?? plans.FreeFor(org).Id;
            // A switch to a smaller plan: the service already bills the new price from the renewal, but the plan paid for stays
            // until then. Once that time has come (or the service reports another plan), the switch is done.
            string? nextPlan = r?.NextPlanId; DateTimeOffset? nextFrom = r?.NextPlanFrom;
            if (nextPlan is not null && r is not null)
            {
                if (planId == nextPlan) planId = r.PlanId;
                else if (planId != r.PlanId) { nextPlan = null; nextFrom = null; } // changed some other way (the service's own page)
            }
            var next = new PlanRecord(id, planId!, n.Status ?? r?.Status ?? PlanStatus.Active, payments.Name,
                n.CustomerId ?? r?.CustomerId, n.SubscriptionId ?? r?.SubscriptionId, n.PeriodEnd ?? r?.PeriodEnd,
                n.CancelAtPeriodEnd ?? r?.CancelAtPeriodEnd ?? false, clock.GetUtcNow(), nextPlan, nextFrom);
            if (r is not null && r with { UpdatedAt = next.UpdatedAt } == next) return; // nothing changed (the service repeats itself)
            await stores.Plans.UpsertAsync(id, next);
            await stores.Audit.AppendAsync(null, "system", "platform", null, "plan.changed",
                AuditChain.Details(("account", org ? "organisation" : id.ToString()), ("plan", next.PlanId), ("status", next.Status.ToString()),
                    ("cancelAtPeriodEnd", next.CancelAtPeriodEnd), ("periodEnd", next.PeriodEnd?.ToString("yyyy-MM-dd")), ("paymentService", payments.Name), ("notice", n.Kind)));
        }
        finally { gate.Release(); }
    }
}
