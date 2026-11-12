namespace VerbaFlow.Core.Domain;

/// <summary>One language a recording can be in. <paramref name="Supported"/> is false for languages no speech service can transcribe yet.
/// <paramref name="Service"/> is "azure" (Azure AI Speech) or "google" (Google Speech-to-Text, for South African languages Azure
/// does not have). A Google language has no live text (Speech mode, Widget), no speaker labels from the service, and must be the
/// only language of its recording.</summary>
public sealed record SpokenLanguage(string Code, string Name, bool Supported = true, string Service = "azure")
{
    public bool Live => Service == "azure";
}

/// <summary>
/// The languages offered when recording or importing. Codes are the locale names the speech service uses. The supported ones were
/// checked against Microsoft's list of fast-transcription locales (October 2026); six South African languages that are not on
/// that list go to Google instead, and two that no service does well are shown but cannot be chosen.
/// </summary>
public static class SpokenLanguageCatalogue
{
    /// <summary>The most languages that can be marked as spoken in one recording. A precise short list also makes detection more accurate.</summary>
    public const int MaxPerRecording = 3;

    public static readonly IReadOnlyList<SpokenLanguage> All =
    [
        new("en-GB", "English (United Kingdom)"), new("en-ZA", "English (South Africa)"), new("en-US", "English (United States)"),
        new("fr-FR", "French (France)"), new("fr-BE", "French (Belgium)"), new("fr-CH", "French (Switzerland)"), new("fr-CA", "French (Canada)"),
        new("es-ES", "Spanish (Spain)"), new("es-MX", "Spanish (Mexico)"),
        new("de-DE", "German (Germany)"), new("de-CH", "German (Switzerland)"), new("de-AT", "German (Austria)"),
        new("it-IT", "Italian (Italy)"), new("it-CH", "Italian (Switzerland)"),
        new("nl-NL", "Dutch (Netherlands)"), new("nl-BE", "Dutch / Flemish (Belgium)"),
        new("mt-MT", "Maltese"), new("pl-PL", "Polish"), new("uk-UA", "Ukrainian"), new("ru-RU", "Russian"),
        new("af-ZA", "Afrikaans"), new("zu-ZA", "isiZulu"),
        new("zh-CN", "Mandarin Chinese (Simplified)"), new("ja-JP", "Japanese"),
        new("ar-SA", "Arabic (Saudi Arabia)"), new("ar-AE", "Arabic (United Arab Emirates)"), new("ar-EG", "Arabic (Egypt)"),
        new("ar-MA", "Arabic (Morocco)"), new("ar-DZ", "Arabic (Algeria)"), new("ar-TN", "Arabic (Tunisia)"), new("ar-LY", "Arabic (Libya)"),
        new("ar-IQ", "Arabic (Iraq)"), new("ar-JO", "Arabic (Jordan)"), new("ar-LB", "Arabic (Lebanon)"), new("ar-SY", "Arabic (Syria)"),
        new("ar-KW", "Arabic (Kuwait)"), new("ar-QA", "Arabic (Qatar)"), new("ar-BH", "Arabic (Bahrain)"), new("ar-OM", "Arabic (Oman)"),
        new("ar-YE", "Arabic (Yemen)"), new("ar-PS", "Arabic (Palestine)"), new("ar-IL", "Arabic (Israel)"),
        // Through Google Speech-to-Text (EU), checked 2026-11-04 and scored on public recordings (docs/south-african-languages.md).
        new("xh-ZA", "isiXhosa", Service: "google"), new("nso-ZA", "Northern Sotho (Sepedi)", Service: "google"),
        new("tn-ZA", "Setswana", Service: "google"), new("ss-ZA", "siSwati", Service: "google"),
        new("ts-ZA", "Xitsonga", Service: "google"), new("ve-ZA", "Tshivenda", Service: "google"),
        // Sesotho: Google got 62% of words wrong in the trial, so it is not offered. isiNdebele: no service has it.
        new("st-ZA", "Sesotho", false), new("nr-ZA", "isiNdebele", false),
    ];

    /// <summary>A plain name for a stored code, for documents: "en-ZA" becomes "English (South Africa)", "en" becomes "English".</summary>
    public static string NameOf(string code) =>
        All.FirstOrDefault(l => l.Code.Equals(code, StringComparison.OrdinalIgnoreCase))?.Name
        ?? code.ToLowerInvariant() switch { "en" => "English", "fr" => "French", "es" => "Spanish", "de" => "German", "it" => "Italian", "nl" => "Dutch", _ => code };

    /// <summary>The catalogue entry for a stored code, or null (older short codes, "auto").</summary>
    public static SpokenLanguage? Find(string code) => All.FirstOrDefault(l => l.Code.Equals(code.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>True when a language goes to Google rather than Azure.</summary>
    public static bool IsGoogle(string code) => Find(code)?.Service == "google";

    /// <summary>A Google language must be the only one: the recording cannot be cut into pieces and sent to two services.</summary>
    public static void CheckCombination(IReadOnlyCollection<string> codes)
    {
        if (codes.Count < 2) return;
        var google = codes.Select(Find).FirstOrDefault(l => l?.Service == "google");
        if (google is not null)
            throw new DomainException($"{google.Name} must be the only language of a recording. Remove the other language, or record the parts separately.");
    }

    private static readonly HashSet<string> LegacyShort = new(StringComparer.OrdinalIgnoreCase) { "en", "fr", "es", "de", "it", "nl" };

    /// <summary>
    /// The stored form of one language choice: "auto", an older short code ("en", "fr"), or a supported locale in its proper spelling.
    /// Null means it is not a language we know. A known language the speech service cannot do yet is refused with a plain sentence.
    /// </summary>
    public static string? Normalise(string? code)
    {
        var c = (code ?? "").Trim();
        if (c.Length == 0) return null;
        if (c.Equals("auto", StringComparison.OrdinalIgnoreCase)) return "auto";
        if (LegacyShort.Contains(c)) return c.ToLowerInvariant();
        var hit = All.FirstOrDefault(l => l.Code.Equals(c, StringComparison.OrdinalIgnoreCase));
        if (hit is null) return null;
        if (!hit.Supported) throw new DomainException($"{hit.Name} cannot be transcribed yet. Choose another language, or let the app detect it automatically.");
        return hit.Code;
    }
}
