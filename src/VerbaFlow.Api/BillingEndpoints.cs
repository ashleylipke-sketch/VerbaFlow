using VerbaFlow.Core.Domain;
using VerbaFlow.Core.Providers;
using VerbaFlow.Infrastructure.Services;
using VerbaFlow.Infrastructure.StandIns;

namespace VerbaFlow.Api;

/// <summary>Plans, minutes used, paying (personal accounts) and the payment service's notices.</summary>
public static class BillingEndpoints
{
    public sealed record CheckoutBody(string? Plan);
    public sealed record OrganisationPlanBody(string? Plan, string? CustomerId, Guid? Organisation = null);
    public sealed record PretendCancelBody(string? How);
    public sealed record StudentBody(string? Email, string? Code);

    /// <summary>Where the payment pages send the person back to: the configured address online, else the address they used.</summary>
    public static string Origin(HttpContext h, IConfiguration config) =>
        config["Notifications:SiteAddress"] is { Length: > 0 } site ? site.TrimEnd('/') : $"{h.Request.Scheme}://{h.Request.Host}";

    public static void MapBillingEndpoints(this WebApplication app, RouteGroupBuilder api)
    {
        api.MapGet("/plan", (HttpContext h, BillingService b, Guid? organisation) => b.ViewAsync(h.Current(), organisation));
        api.MapPost("/plan/checkout", async (HttpContext h, BillingService b, IConfiguration c, CheckoutBody body, CancellationToken ct) =>
            Results.Ok(new { url = (await b.CheckoutAsync(h.Current(), body.Plan, Origin(h, c), ct)).Url }));
        api.MapPost("/plan/manage", async (HttpContext h, BillingService b, IConfiguration c, CancellationToken ct) =>
            Results.Ok(new { url = (await b.ManageAsync(h.Current(), Origin(h, c), ct)).Url }));
        // Switching a paid personal plan to another one, on the same card.
        api.MapPost("/plan/change", async (HttpContext h, BillingService b, FailureReporter reporter, CheckoutBody body, CancellationToken ct) =>
        {
            try { return Results.Ok(await b.ChangePlanAsync(h.Current(), body.Plan, ct)); }
            catch (ProviderException ex)
            {
                var rep = await reporter.ReportAsync("payments", null, ex);
                return Results.Json(new { error = $"The plan could not be switched. If you chose a bigger plan, check the card under Manage the plan and try again. (Reference {rep.Reference})" }, statusCode: 502);
            }
        });

        // The Student plan: a code is emailed to a school, college or university address and typed back.
        api.MapPost("/plan/student/start", async (HttpContext h, BillingService b, StudentCodes codes, IEmailSender mail, FailureReporter reporter, StudentBody body, CancellationToken ct) =>
        {
            var user = h.Current();
            if (!user.Individual) throw new ForbiddenException("Only a personal account can choose the Student plan.");
            var email = EmailCodes.Clean(body.Email);
            if (email is null) throw new DomainException("That does not look like an email address. Check it and try again.");
            if (!b.IsStudentAddress(email))
                throw new DomainException("That address does not look like a school, college or university address (for example ending in .ac.uk, .ac.za or .edu). Use the address your place of study gave you. If yours is not recognised, contact VerbaFlow.");
            var code = codes.Codes.Make(user.Id + ":" + email);
            if (code is null) throw new DomainException("A code was sent a moment ago. Check that email (and the junk folder), or try again in a minute.");
            try
            {
                await mail.SendAsync(new EmailMessage([email], $"Your VerbaFlow student code: {code}",
                    $"Your code to confirm you are a student on VerbaFlow is:\n\n    {code}\n\nType it on VerbaFlow's Your plan page within 10 minutes.\n\n" +
                    "If you did not ask for this, you can ignore this email.\n", []), ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                var rep = await reporter.ReportAsync("student-check", null, ex);
                return Results.Json(new { error = $"The code could not be emailed. Try again later. (Reference {rep.Reference})" }, statusCode: 502);
            }
            return Results.Ok(new { sent = true });
        });
        api.MapPost("/plan/student/finish", async (HttpContext h, BillingService b, StudentCodes codes, StudentBody body, CancellationToken ct) =>
        {
            var user = h.Current();
            var email = EmailCodes.Clean(body.Email);
            if (email is null || !codes.Codes.Check(user.Id + ":" + email, body.Code))
                throw new DomainException("That code is not right, or it has run out. Check the latest email, or ask for a new code.");
            return Results.Ok(await b.RecordStudentAsync(user, email, ct));
        });
        api.MapPost("/plan/organisation", (HttpContext h, BillingService b, OrganisationPlanBody body) =>
            b.SetOrganisationPlanAsync(h.Current(), body.Plan, body.CustomerId, body.Organisation));

        // The organisations using VerbaFlow: only VerbaFlow support sees and changes them.
        api.MapGet("/organisations", (HttpContext h, OrganisationService o, BillingService b) => o.ListAsync(h.Current(), b));
        api.MapPost("/organisations", async (HttpContext h, OrganisationService o, OrganisationBody body) => Results.Ok(await o.CreateAsync(h.Current(), body)));
        api.MapPut("/organisations/{id:guid}", async (HttpContext h, OrganisationService o, Guid id, OrganisationBody body) => Results.Ok(await o.ChangeAsync(h.Current(), id, body)));

        // The stand-in's pretend payment pages (only while no payment service is set up).
        api.MapPost("/plan/pretend/{reference}/pay", async (HttpContext h, BillingService b, string reference) =>
        {
            if (b.Payments is not StandInPayments pretend) return Results.NotFound();
            var notice = pretend.Pay(h.Current().Id, reference);
            if (notice is null) throw new DomainException("This payment page has run out or was already used. Go back to Your plan and try again.");
            await b.ApplyAsync(notice);
            return Results.Ok(await b.ViewAsync(h.Current()));
        });
        api.MapPost("/plan/pretend/cancel", async (HttpContext h, BillingService b, PretendCancelBody body) =>
        {
            if (b.Payments is not StandInPayments pretend) return Results.NotFound();
            var user = h.Current();
            if (!user.Individual) throw new ForbiddenException("Only a personal account pays by card.");
            await b.ApplyAsync(pretend.Cancel(BillingService.AccountFor(user), body.How ?? "end"));
            return Results.Ok(await b.ViewAsync(user));
        });

        // Stripe's signed notices. Public (Stripe is not signed in); the signature over the exact body is what is trusted.
        app.MapPost("/api/payments/stripe", async (HttpContext h, BillingService b, FailureReporter reporter) =>
        {
            using var reader = new StreamReader(h.Request.Body);
            var body = await reader.ReadToEndAsync();
            if (body.Length > 1_000_000) return Results.StatusCode(413);
            try
            {
                return await b.HandleNoticeAsync(body, h.Request.Headers["Stripe-Signature"].FirstOrDefault()) ? Results.Ok() : Results.BadRequest();
            }
            catch (Exception ex)
            {
                await reporter.ReportAsync("payments", null, ex);
                return Results.StatusCode(500); // Stripe sends it again later
            }
        });
    }
}

/// <summary>Student codes, kept apart from sign-in codes (same rules: 10 minutes, 5 wrong guesses, 1 a minute, 5 an hour).</summary>
public sealed class StudentCodes(TimeProvider clock)
{
    public EmailCodes Codes { get; } = new(clock);
}

/// <summary>Every few minutes, moves Student plans whose check has run out, and passes the organisation's minutes to the payment service for its next invoice.</summary>
public sealed class UsageWorker(BillingService billing, FailureReporter reporter) : BackgroundService
{
    public static readonly TimeSpan Every = TimeSpan.FromMinutes(5);

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(Every);
        while (await timer.WaitForNextTickAsync(ct))
        {
            try { await billing.SendUsageAsync(ct); await billing.StudentRenewalsAsync(ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex) { await reporter.ReportAsync("usage", null, ex); }
        }
    }
}
