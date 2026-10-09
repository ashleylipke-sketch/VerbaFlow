using System.Security.Cryptography;

namespace VerbaFlow.Core.Domain;

/// <summary>What kind of problem a connected service had. Decides the plain-language message customers see.</summary>
public enum FaultKind { Other, Busy, Unavailable, Settings, Audio, Blocked }

/// <summary>A connected service (Azure Speech, Azure OpenAI) refused or failed. The message is technical and for support only.</summary>
public sealed class ProviderException(FaultKind kind, string message) : InvalidOperationException(message)
{
    public FaultKind Kind { get; } = kind;
}

/// <summary>
/// What customers see when something fails inside the product: a plain sentence and a short reference code.
/// Technical detail never reaches them, whatever their role, including administrators. Support finds the detail from the code.
/// </summary>
public static class CustomerMessages
{
    public static string NewReference()
    {
        const string alphabet = "23456789ABCDEFGHJKMNPQRSTUVWXYZ"; // no 0/O/1/I/L, easy to read out on the phone
        var bytes = RandomNumberGenerator.GetBytes(6);
        return "VF-" + new string(bytes.Select(b => alphabet[b % alphabet.Length]).ToArray());
    }

    /// <param name="service">"transcription" or "summary" or another plain word for what failed.</param>
    public static string For(FaultKind kind, string service, string reference)
    {
        var text = kind switch
        {
            FaultKind.Busy => $"The {service} service is busy right now. Wait a few minutes and try again.",
            FaultKind.Unavailable => $"The {service} service could not be reached. Try again in a few minutes.",
            FaultKind.Settings => $"The {service} service is not available at the moment. Please contact support.",
            FaultKind.Audio => "This recording could not be processed. Try recording or importing it again, or contact support.",
            FaultKind.Blocked => "A summary could not be created for this meeting. Contact support if you need one.",
            _ => "Something went wrong. Please try again, and contact support if it happens again.",
        };
        return $"{text} Reference: {reference}.";
    }
}

/// <summary>The technical record of a failure. Only support staff can read these.</summary>
public sealed record SupportError(Guid Id, string Reference, string Area, string Kind, Guid? ItemId, string Detail, DateTimeOffset At);
