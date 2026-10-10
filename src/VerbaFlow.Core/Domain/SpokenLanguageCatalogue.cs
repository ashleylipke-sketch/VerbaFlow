namespace VerbaFlow.Core.Domain;

/// <summary>One language a recording can be in. <paramref name="Supported"/> is false for languages the speech service cannot transcribe yet.</summary>
public sealed record SpokenLanguage(string Code, string Name, bool Supported = true);

/// <summary>
/// The languages offered when recording or importing. Codes are the locale names the speech service uses. The supported ones were
/// checked against Microsoft's list of fast-transcription locales (October 2026); the South African languages at the end are
/// not on that list, so they are shown but cannot be chosen.
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
        new("xh-ZA", "isiXhosa", false), new("st-ZA", "Sesotho", false), new("tn-ZA", "Setswana", false), new("nso-ZA", "Northern Sotho", false),
        new("ts-ZA", "Xitsonga", false), new("ss-ZA", "siSwati", false), new("ve-ZA", "Tshivenda", false), new("nr-ZA", "isiNdebele", false),
    ];

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
