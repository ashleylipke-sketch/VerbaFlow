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
    /// <summary>In Chrome and Edge, the shortcut is left to the browser Widget, which knows the website (and its blocks).</summary>
    public bool LeaveBrowsersToExtension { get; init; } = true;
    [JsonConverter(typeof(JsonStringEnumConverter<ParagraphGap>))]
    public ParagraphGap ParagraphGap { get; init; } = ParagraphGap.Automatic;
    /// <summary>The keyboard shortcut, as "Alt+Shift+D".</summary>
    public string Shortcut { get; init; } = VerbaFlow.Widget.Shortcut.Default.ToString();
    /// <summary>The way chosen for a program (by its file name, lower case), where it differs from the usual one.</summary>
    public Dictionary<string, InsertMethod> InsertMethods { get; init; } = [];

    /// <summary>Same settings: the programs' ways compared by what they hold.</summary>
    public bool Equals(WidgetSettings? o) => o is not null && Server == o.Server && TestUser == o.TestUser && Languages == o.Languages
        && LeaveBrowsersToExtension == o.LeaveBrowsersToExtension && ParagraphGap == o.ParagraphGap && Shortcut == o.Shortcut
        && InsertMethods.Count == o.InsertMethods.Count && InsertMethods.All(x => o.InsertMethods.TryGetValue(x.Key, out var m) && m == x.Value);

    public override int GetHashCode() => HashCode.Combine(Server, TestUser, Languages, LeaveBrowsersToExtension, ParagraphGap, Shortcut, InsertMethods.Count);

    /// <summary>The shortcut to use (the default when the stored one is not allowed).</summary>
    [JsonIgnore]
    public Shortcut Keys => VerbaFlow.Widget.Shortcut.Parse(Shortcut) ?? VerbaFlow.Widget.Shortcut.Default;

    /// <summary>How the words go into this program: the person's choice, else the usual way for it.</summary>
    public InsertMethod MethodFor(string program) =>
        InsertMethods.TryGetValue(program.ToLowerInvariant(), out var m) ? m : Insertion.DefaultFor(program);

    /// <summary>These settings with this program's way chosen; choosing the usual way forgets the choice.</summary>
    public WidgetSettings WithMethod(string program, InsertMethod method)
    {
        var key = program.ToLowerInvariant();
        var map = new Dictionary<string, InsertMethod>(InsertMethods);
        if (method == Insertion.DefaultFor(key)) map.Remove(key); else map[key] = method;
        return this with { InsertMethods = map };
    }

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true, Converters = { new JsonStringEnumConverter<InsertMethod>() } };

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
        Shortcut = Keys.ToString(),
        InsertMethods = (InsertMethods ?? []).Where(x => x.Key.Trim().Length > 0 && Enum.IsDefined(x.Value))
            .GroupBy(x => x.Key.Trim().ToLowerInvariant()).ToDictionary(g => g.Key, g => g.Last().Value),
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
