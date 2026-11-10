using System.Collections.Concurrent;
using VerbaFlow.Core.Domain;
using VerbaFlow.Core.Providers;

namespace VerbaFlow.Infrastructure.StandIns;

/// <summary>
/// Pretend card payments, used until a Stripe account is set up: Upgrade opens VerbaFlow's own "pretend payment" page,
/// nothing is charged, and the plan starts at once for a month. Manage opens a page that can cancel it.
/// </summary>
public sealed class StandInPayments(TimeProvider clock) : IPayments
{
    private readonly ConcurrentDictionary<string, (Guid User, Guid Account, string Plan, DateTimeOffset Until)> pending = new();
    public string Name => "stand-in";
    public bool Pretend => true;

    public Task<PaymentPage> CheckoutAsync(User user, Guid account, Plan plan, string returnUrl, CancellationToken ct)
    {
        var reference = "pay_" + Guid.NewGuid().ToString("N")[..12];
        pending[reference] = (user.Id, account, plan.Id, clock.GetUtcNow().AddHours(1));
        return Task.FromResult(new PaymentPage($"/#/plan/pretend-pay/{reference}", reference));
    }

    public Task<PaymentPage> ManageAsync(PlanRecord record, string returnUrl, CancellationToken ct) =>
        Task.FromResult(new PaymentPage("/#/plan/pretend-manage", record.SubscriptionId ?? ""));

    public Task<PaymentNotice?> ChangeAsync(PlanRecord record, Plan to, bool now, CancellationToken ct) =>
        Task.FromResult<PaymentNotice?>(now ? new PaymentNotice("pretend.changed", record.AccountId, to.Id, PlanStatus.Active, null, null, null, false) : null);

    /// <summary>The stand-in has no signed notices; its pages call <see cref="Pay"/> and <see cref="Cancel"/> instead.</summary>
    public PaymentNotice? Read(string body, string? signature) => null;

    /// <summary>The person pressed Pretend to pay: the notice a real service would send. Null for an unknown or someone else's reference.</summary>
    public PaymentNotice? Pay(Guid userId, string reference)
    {
        if (!pending.TryGetValue(reference, out var p) || p.User != userId || p.Until < clock.GetUtcNow() || !pending.TryRemove(reference, out _)) return null;
        return new PaymentNotice("pretend.paid", p.Account, p.Plan, PlanStatus.Active, "cus_pretend_" + p.Account.ToString("N")[..8],
            "sub_pretend_" + reference[4..], clock.GetUtcNow().AddMonths(1), false);
    }

    /// <summary>Cancel at the end of the month (or take that back), or end the plan now.</summary>
    public PaymentNotice Cancel(Guid account, string how) => how switch
    {
        "now" => new PaymentNotice("pretend.ended", account, null, PlanStatus.Cancelled, null, null, clock.GetUtcNow(), false),
        "keep" => new PaymentNotice("pretend.kept", account, null, null, null, null, null, false),
        _ => new PaymentNotice("pretend.cancelled", account, null, null, null, null, null, true),
    };
}
