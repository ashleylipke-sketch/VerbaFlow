using System.Net;
using Microsoft.Extensions.Logging;
using VerbaFlow.Core.Domain;

namespace VerbaFlow.Infrastructure.Services;

public sealed record ReportedFailure(string Reference, FaultKind Kind, string UserMessage);

/// <summary>
/// The one place technical failures are handled. The customer gets a plain message with a reference code. The technical
/// detail goes to the server log and to the support-only error table, filed under the same code.
/// </summary>
public sealed class FailureReporter(Stores stores, TimeProvider clock, ILogger<FailureReporter>? log = null)
{
    public async Task<ReportedFailure> ReportAsync(string area, Guid? itemId, Exception ex)
    {
        var kind = Classify(ex);
        var reference = CustomerMessages.NewReference();
        var detail = ex.ToString();
        if (detail.Length > 4000) detail = detail[..4000];
        log?.LogError(ex, "{Reference} {Area} failure ({Kind}) on item {ItemId}", reference, area, kind, itemId);
        try
        {
            var id = Guid.NewGuid();
            await stores.SupportErrors.UpsertAsync(id, new SupportError(id, reference, area, kind.ToString(), itemId, detail, clock.GetUtcNow()));
        }
        catch (Exception storeEx) { log?.LogError(storeEx, "{Reference} could not be written to the support error table", reference); }
        var service = area switch { "summary" => "summary", "transcription" => "transcription", _ => "VerbaFlow" };
        return new ReportedFailure(reference, kind, CustomerMessages.For(kind, service, reference));
    }

    public static FaultKind Classify(Exception ex) => ex switch
    {
        ProviderException p => p.Kind,
        HttpRequestException => FaultKind.Unavailable,
        TaskCanceledException or TimeoutException => FaultKind.Unavailable,
        _ => FaultKind.Other,
    };

    /// <summary>The kind of problem for an HTTP status from a connected service.</summary>
    public static FaultKind ForStatus(HttpStatusCode code) => code switch
    {
        HttpStatusCode.TooManyRequests => FaultKind.Busy,
        HttpStatusCode.InternalServerError or HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout => FaultKind.Unavailable,
        HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.NotFound => FaultKind.Settings,
        HttpStatusCode.BadRequest or HttpStatusCode.RequestEntityTooLarge or HttpStatusCode.UnsupportedMediaType => FaultKind.Audio,
        _ => FaultKind.Other,
    };
}
