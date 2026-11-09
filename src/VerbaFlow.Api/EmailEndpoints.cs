using System.Collections.Concurrent;
using VerbaFlow.Core.Audit;
using VerbaFlow.Core.Domain;
using VerbaFlow.Core.Providers;
using VerbaFlow.Infrastructure.Persistence;
using VerbaFlow.Infrastructure.Services;

namespace VerbaFlow.Api;

public sealed record EmailTestBody(string? To);

/// <summary>Administrators can see which email service VerbaFlow uses and send themselves a test email (docs/azure-email-setup.md).</summary>
public static class EmailEndpoints
{
    public const int TestsPerHour = 10;
    private static readonly ConcurrentDictionary<Guid, List<DateTimeOffset>> tests = new();

    public static void MapEmailEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/email-service", (HttpContext h, IEmailSender mail, IConfiguration config) =>
        {
            var actor = h.Current();
            if (!actor.IsAdmin) throw new ForbiddenException("Only administrators can see the email settings.");
            var real = mail is not VerbaFlow.Infrastructure.StandIns.StandInEmailSender;
            return Results.Ok(new { real, sender = real ? config["Email:Sender"] : null, yourEmail = string.IsNullOrWhiteSpace(actor.Email) ? null : actor.Email });
        });

        api.MapPost("/email-service/test", async (HttpContext h, IEmailSender mail, Stores stores, FailureReporter reporter, TimeProvider clock,
            EmailTestBody b, CancellationToken ct) =>
        {
            var actor = h.Current();
            if (!actor.IsAdmin) throw new ForbiddenException("Only administrators can send a test email.");
            var to = EmailCodes.Clean(string.IsNullOrWhiteSpace(b.To) ? actor.Email : b.To)
                ?? throw new DomainException("That does not look like an email address. Check it and try again.");
            var now = clock.GetUtcNow();
            var mine = tests.GetOrAdd(actor.Id, _ => []);
            lock (mine)
            {
                mine.RemoveAll(t => now - t > TimeSpan.FromHours(1));
                if (mine.Count >= TestsPerHour) throw new DomainException($"You have sent {TestsPerHour} test emails in the last hour. Try again later.");
                mine.Add(now);
            }
            var message = new EmailMessage([to], "VerbaFlow test email",
                $"This is a test email from VerbaFlow, sent by {actor.Name}.\n\n" +
                "If you can read this, VerbaFlow can send email: sign-in codes, notifications and exported minutes will arrive the same way.\n\n" +
                "If it landed in your junk folder, mark it as not junk so later emails arrive in the inbox.\n", []);
            EmailCheck check;
            try { check = await mail.SendAndWaitAsync(message, ct); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                var rep = await reporter.ReportAsync("email-test", null, ex);
                await Audit(stores, actor, "email.test_failed", ("to", to), ("service", mail.Name), ("reference", rep.Reference));
                return Results.Json(new { error = $"The test email could not be sent. (Reference {rep.Reference})" }, statusCode: 502);
            }
            if (check.Outcome == EmailOutcome.Failed)
            {
                var rep = await reporter.ReportAsync("email-test", null, new ProviderException(FaultKind.Other, check.Problem ?? "Email failed."));
                await Audit(stores, actor, "email.test_failed", ("to", to), ("service", mail.Name), ("mailReference", check.Reference), ("reference", rep.Reference));
                return Results.Json(new { error = $"The email service took the test email but could not deliver it. (Reference {rep.Reference})" }, statusCode: 502);
            }
            var result = check.Outcome switch { EmailOutcome.HandedOver => "handed over for delivery", EmailOutcome.StillWaiting => "accepted, still on its way", _ => "saved to the outbox" };
            await Audit(stores, actor, "email.test_sent", ("to", to), ("service", mail.Name), ("result", result), ("mailReference", check.Reference));
            return Results.Ok(new { to, outcome = check.Outcome.ToString() });
        });
    }

    private static Task Audit(Stores stores, User actor, string action, params (string, object?)[] details) =>
        stores.Audit.AppendAsync(actor.Id, "admin", "platform", null, action, AuditChain.Details(details));
}
