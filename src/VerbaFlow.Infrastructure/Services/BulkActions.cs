using VerbaFlow.Core.Domain;
using VerbaFlow.Core.Security;

namespace VerbaFlow.Infrastructure.Services;

/// <summary>What happened to one ticked recording.</summary>
public sealed record BulkResult(Guid Id, string Name, bool Done, string? Problem);

/// <summary>
/// The same action on several ticked recordings: assign (or take back), accept, return or approve. Each recording goes through
/// exactly the same checks and History entry as when done one at a time, so one refusal does not stop the others.
/// </summary>
public sealed class BulkActions(MeetingService meetings, Stores stores, FailureReporter reporter)
{
    public const int Max = 100;
    public static readonly string[] Actions = ["assign", "accept", "return", "approve"];

    public async Task<IReadOnlyList<BulkResult>> RunAsync(User actor, string? action, IReadOnlyList<Guid>? ids, Guid? toUserId, string? reason)
    {
        var a = action?.Trim().ToLowerInvariant();
        if (a is null || !Actions.Contains(a)) throw new DomainException("Choose what to do with the ticked recordings.");
        var list = (ids ?? []).Distinct().ToList();
        if (list.Count == 0) throw new DomainException("Tick at least one recording first.");
        if (list.Count > Max) throw new DomainException($"Tick at most {Max} recordings at a time.");
        if (a == "assign" && toUserId is { } to) await stores.RequireUserAsync(to);

        var results = new List<BulkResult>();
        foreach (var id in list)
        {
            var item = await stores.Items.GetAsync(id);
            // Never show the name of a recording this person cannot open.
            var name = item is null || !PermissionEvaluator.CanSee(actor, item) ? "(a recording you cannot open)"
                : string.IsNullOrWhiteSpace(item.Name) ? "(no name)" : item.Name;
            try
            {
                switch (a)
                {
                    case "assign": await meetings.AssignAsync(actor, id, toUserId, reason); break;
                    case "accept": await meetings.AcceptAsync(actor, id); break;
                    case "return": await meetings.ReturnAsync(actor, id); break;
                    case "approve": await meetings.ApproveAsync(actor, id); break;
                }
                results.Add(new(id, name, true, null));
            }
            catch (DomainException ex) { results.Add(new(id, name, false, ex.Message)); }
            catch (ForbiddenException) { results.Add(new(id, name, false, "You are not allowed to do this with this recording.")); }
            catch (NotFoundException) { results.Add(new(id, name, false, "This recording could not be found.")); }
            catch (Exception ex)
            {
                var f = await reporter.ReportAsync("bulk", id, ex);
                results.Add(new(id, name, false, f.UserMessage));
            }
        }
        return results;
    }
}
