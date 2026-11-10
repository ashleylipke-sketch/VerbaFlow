namespace VerbaFlow.Core.Domain;

/// <summary>
/// A plan: how many minutes of speech a month are included, and its price.
/// Personal plans are paid by card; when their minutes are used up, new conversions wait until the next month or a bigger plan.
/// <see cref="ForOrganisation"/> plans are a subscription the organisation is invoiced for each month: its people share the minutes
/// (<see cref="People"/> is how many it is meant for, shown, not enforced), and minutes over the included ones are never refused but
/// added to the next invoice at <see cref="ExtraPerHour"/>. The organisation's Trial has no invoice, so it stops at its minutes.
/// </summary>
public sealed record Plan(string Id, string Name, int MinutesPerMonth, string Price, string? StripePriceId = null,
    bool ForOrganisation = false, int? People = null, decimal? ExtraPerHour = null)
{
    /// <summary>The plan an account is on when it pays for none: Free for personal accounts, Trial for the organisation.</summary>
    public bool Free => Id is Plans.FreeId or Plans.TrialId;
    /// <summary>Extra minutes are invoiced rather than refused (an organisation's paid subscription).</summary>
    public bool InvoicesExtra => ForOrganisation && !Free;
    public long IncludedMs => MinutesPerMonth * 60_000L;
}

/// <summary>The plans on offer: one free personal plan ("free") and one organisation trial ("org-trial"), plus paid ones.</summary>
public sealed class Plans
{
    public const string FreeId = "free", TrialId = "org-trial", StudentId = "personal-student", PersonalBasicId = "personal-basic";

    public Plans(IReadOnlyList<Plan> all)
    {
        if (all.Count(p => p.Id == FreeId && !p.ForOrganisation) != 1 || all.Count(p => p.Id == TrialId && p.ForOrganisation) != 1)
            throw new ArgumentException("There must be a free personal plan and an organisation trial.");
        if (all.Select(p => p.Id).Distinct().Count() != all.Count) throw new ArgumentException("Plan ids must differ.");
        All = all;
    }
    public IReadOnlyList<Plan> All { get; }
    public IEnumerable<Plan> For(bool organisation) => All.Where(p => p.ForOrganisation == organisation);
    public Plan FreeFor(bool organisation) => Find(organisation ? TrialId : FreeId)!;
    public Plan? Find(string? id) => All.FirstOrDefault(p => p.Id == id);
    public Plan Of(PlanRecord? r, bool organisation) =>
        r is { Status: PlanStatus.Active or PlanStatus.PastDue } && Find(r.PlanId) is { } p && p.ForOrganisation == organisation ? p : FreeFor(organisation);

    /// <summary>
    /// Placeholder plans used when settings change none (prices and minutes are not yet decided by the owner).
    /// Personal (card): Free 30 minutes; Basic £6.99 a month for 5 hours; Student £3.99 for 5 hours
    /// (not checked that the person is a student); Professional £14.99 for 20 hours.
    /// Organisation (invoiced monthly, minutes shared): Trial 2 hours; Basic £49 for 100 hours (up to 25 people); Business £179 for
    /// 400 hours (up to 100 people); Professional £399 for 1,000 hours (up to 250 people); Enterprise price agreed, 3,000 hours
    /// to start (no people limit shown); extra hours on the next invoice at £1.20, £1.10, £1.00 and £0.90.
    /// </summary>
    public static Plans Default() => new([
        new Plan(FreeId, "Free", 30, "£0"),
        new Plan("personal-basic", "Basic", 300, "£6.99 a month", "stand-in-personal-basic"),
        new Plan("personal-student", "Student", 300, "£3.99 a month", "stand-in-personal-student"),
        new Plan("personal-professional", "Professional", 1_200, "£14.99 a month", "stand-in-personal-professional"),
        new Plan(TrialId, "Trial", 120, "£0", null, true, 5),
        new Plan("basic", "Basic", 6_000, "£49 a month", null, true, 25, 1.20m),
        new Plan("business", "Business", 24_000, "£179 a month", null, true, 100, 1.10m),
        new Plan("professional", "Professional", 60_000, "£399 a month", null, true, 250, 1.00m),
        new Plan("enterprise", "Enterprise", 180_000, "Agreed with VerbaFlow", null, true, null, 0.90m),
    ]);

    /// <summary>
    /// The default plans with any changes from settings (Billing:Plans:&lt;id&gt;:Name, MinutesPerMonth, Price, People, ExtraPerHour,
    /// StripePriceId). Plans cannot be added or removed this way, only changed.
    /// </summary>
    public static Plans With(Func<string, string?> setting)
    {
        var all = Default().All.Select(p =>
        {
            string? S(string key) => setting($"Billing:Plans:{p.Id}:{key}") is { Length: > 0 } v ? v.Trim() : null;
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            return p with
            {
                Name = S("Name") ?? p.Name,
                MinutesPerMonth = int.TryParse(S("MinutesPerMonth"), inv, out var m) && m >= 0 ? m : p.MinutesPerMonth,
                Price = S("Price") ?? p.Price,
                People = int.TryParse(S("People"), inv, out var n) && n > 0 ? n : p.People,
                ExtraPerHour = decimal.TryParse(S("ExtraPerHour"), System.Globalization.NumberStyles.Number, inv, out var x) && x >= 0 && p.ForOrganisation && !p.Free ? x : p.ExtraPerHour,
                StripePriceId = S("StripePriceId") ?? p.StripePriceId,
            };
        }).ToList();
        return new Plans(all);
    }
}

public enum PlanStatus { Active, PastDue, Cancelled }

/// <summary>
/// An account's paid plan, kept by account id (a personal account's user id, or <see cref="BillingAccounts.Organisation"/>). Card details are never seen by VerbaFlow: the payment service keeps them and
/// says when a plan starts, renews or ends. <see cref="CancelAtPeriodEnd"/>: the person has cancelled and keeps the plan until
/// <see cref="PeriodEnd"/>. <see cref="NextPlanId"/>: a smaller plan the person switched to, which starts at <see cref="NextPlanFrom"/>
/// (the end of the month already paid for); until then they keep the plan they paid for.
/// </summary>
public sealed record PlanRecord(Guid AccountId, string PlanId, PlanStatus Status, string Service, string? CustomerId, string? SubscriptionId,
    DateTimeOffset? PeriodEnd, bool CancelAtPeriodEnd, DateTimeOffset UpdatedAt, string? NextPlanId = null, DateTimeOffset? NextPlanFrom = null);

/// <summary>
/// A personal account showed it belongs to a student: a code sent to a school, college or university email address was typed back.
/// Only the address's domain and a SHA-256 of the address are kept (the hash stops one address serving many accounts).
/// Good for <see cref="StudentEmails.Valid"/> after the check; the Student plan needs a check that has not run out.
/// </summary>
public sealed record StudentCheck(Guid UserId, string Domain, string AddressHash, DateTimeOffset CheckedAt, DateTimeOffset Until);

/// <summary>Which email addresses count as a student's (a school, college or university address).</summary>
public static class StudentEmails
{
    public static readonly TimeSpan Valid = TimeSpan.FromDays(365);
    /// <summary>How long before the check runs out the plan page asks for it again.</summary>
    public static readonly TimeSpan AskAgainBefore = TimeSpan.FromDays(30);

    /// <summary>
    /// True for addresses at an education domain: ending in .edu, or with "edu" or "ac" just before a country ending
    /// (.edu.au, .ac.uk, .ac.za, .ac.nz, .edu.za...), or at one of <paramref name="extra"/> (settings Billing:StudentDomains:
    /// many universities, for example in Europe, use plain domains such as uni-heidelberg.de). A domain in <paramref name="extra"/>
    /// also covers its subdomains (student.uva.nl under uva.nl).
    /// </summary>
    public static bool IsStudentAddress(string email, IEnumerable<string>? extra = null)
    {
        var at = email.LastIndexOf('@');
        if (at < 1) return false;
        var domain = email[(at + 1)..].Trim().TrimEnd('.').ToLowerInvariant();
        var parts = domain.Split('.');
        if (parts.Length < 2 || parts.Any(p => p.Length == 0)) return false;
        if (parts[^1] == "edu") return true;
        if (parts.Length >= 3 && parts[^1].Length == 2 && parts[^2] is "edu" or "ac") return true;
        foreach (var raw in extra ?? [])
        {
            var d = raw.Trim().TrimStart('@', '.').TrimEnd('.').ToLowerInvariant();
            if (d.Length > 0 && (domain == d || domain.EndsWith("." + d))) return true;
        }
        return false;
    }

    public static string DomainOf(string email) => email[(email.LastIndexOf('@') + 1)..].Trim().ToLowerInvariant();

    public static string Hash(string email) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes("student:" + email.Trim().ToLowerInvariant())));
}

/// <summary>One use of speech in a month: what it was and how long.</summary>
public sealed record UsageEntry(DateTimeOffset At, string Kind, int Ms, Guid? ItemId, Guid? UserId = null);

/// <summary>
/// Minutes of speech an account turned into text in one calendar month (UTC), with the last uses. Its id is worked out from the
/// account and the month (<see cref="IdOf"/>), so each month has one record.
/// </summary>
/// <param name="UnsentMs">An invoiced organisation's minutes not yet passed to the payment service (sent every few minutes).</param>
/// <param name="SentMs">Of those, what has been passed on, and how many times (each sending has its own number, so none is counted twice).</param>
public sealed record UsageMonth(Guid Id, Guid AccountId, string Month, long Ms, IReadOnlyList<UsageEntry> Entries, IReadOnlyList<int>? Warned = null,
    long UnsentMs = 0, long SentMs = 0, int Sendings = 0)
{
    public const int KeptEntries = 300;
    public static string MonthOf(DateTimeOffset at) => at.UtcDateTime.ToString("yyyy-MM", System.Globalization.CultureInfo.InvariantCulture);

    public static Guid IdOf(Guid userId, string month)
    {
        var hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes($"usage:{userId:N}:{month}"));
        return new Guid(hash.AsSpan(0, 16));
    }

    public UsageMonth Add(UsageEntry e) =>
        this with { Ms = Ms + e.Ms, Entries = [e, .. Entries.Take(KeptEntries - 1)] };
}

/// <summary>Whose minutes a use of speech counts against: a personal account's own, or its organisation's (shared by its people).</summary>
public static class BillingAccounts
{
    /// <summary>The first organisation's account (also its id, <see cref="Organisations.Default"/>). Other organisations' accounts are their ids.</summary>
    public static readonly Guid Organisation = new("0f1c9a4e-0000-4000-8000-000000000001");
    public static Guid Of(User u) => u.Individual ? u.Id : u.OrganisationId ?? Organisation;
    public static Guid Of(Item i) => i.Individual ? i.OwnerId : i.OrganisationId ?? Organisation;
}

/// <summary>Speech cannot be turned into text: the account's plan has no minutes left this month.</summary>
public sealed class PlanLimitException(string message) : Exception(message);
