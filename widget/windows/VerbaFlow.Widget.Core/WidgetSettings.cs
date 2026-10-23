using System.Text.Json;
using System.Text.Json.Serialization;

namespace VerbaFlow.Widget;

/// <summary>The Widget for Windows' settings, kept in the person's own Windows profile. No keys or passwords are kept here.</summary>
public sealed record WidgetSettings
{
    public const string DefaultServer = "http://localhost:5044";

    /// <summary>VerbaFlow's address, without a slash at the end.</summary>
    public string Server { get; init; } = DefaultServer;
    /// <summary>The test user, used only when this VerbaFlow allows test users (on the owner's computer) and nobody signed in with Microsoft.</summary>
    public string TestUser { get; init; } = "alice";
    /// <summary>The language spoken: one locale, or up to three separated by commas.</summary>
    public string Languages { get; init; } = "en-GB";
    /// <summary>In Chrome and Edge, Alt+Shift+D is left to the browser Widget, which knows the website (and its blocks).</summary>
    public bool LeaveBrowsersToExtension { get; init; } = true;
    [JsonConverter(typeof(JsonStringEnumConverter<ParagraphGap>))]
    public ParagraphGap ParagraphGap { get; init; } = ParagraphGap.Automatic;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    /// <summary>%APPDATA%\VerbaFlow\Widget\settings.json</summary>
    public static string DefaultPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VerbaFlow", "Widget", "settings.json");

    public static WidgetSettings Load(string path)
    {
        try
        {
            var s = File.Exists(path) ? JsonSerializer.Deserialize<WidgetSettings>(File.ReadAllText(path), Json) : null;
            return (s ?? new WidgetSettings()).Clean();
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { return new WidgetSettings(); }
    }

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(Clean(), Json));
    }

    public WidgetSettings Clean() => this with
    {
        Server = CleanServer(Server) ?? DefaultServer,
        TestUser = string.IsNullOrWhiteSpace(TestUser) ? "alice" : TestUser.Trim().ToLowerInvariant(),
        Languages = string.IsNullOrWhiteSpace(Languages) ? "en-GB" : string.Join(",", Languages.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)),
    };

    /// <summary>A VerbaFlow address as typed, or null when it is not one (http or https only).</summary>
    public static string? CleanServer(string? typed)
    {
        var t = (typed ?? "").Trim().TrimEnd('/');
        if (t.Length > 0 && !t.Contains("://")) t = (t.StartsWith("localhost") ? "http://" : "https://") + t;
        return Uri.TryCreate(t, UriKind.Absolute, out var u) && (u.Scheme == "http" || u.Scheme == "https") && u.Host.Length > 0
            ? u.GetLeftPart(UriPartial.Path).TrimEnd('/') : null;
    }
}
