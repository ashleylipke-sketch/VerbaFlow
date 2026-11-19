using System.Collections.Specialized;
using System.Diagnostics;

namespace VerbaFlow.Widget;

/// <summary>
/// The saved documents a person can name by voice ("VerbaFlow, open budget", "VerbaFlow, attach budget and invoice"):
/// the files Windows lists as recently used, then those in Documents, Desktop, Downloads and OneDrive (three folders
/// deep), newest first. Read on a background thread and again every two minutes; nothing leaves the computer.
/// </summary>
internal static class Documents
{
    public sealed record Document(string Path, string Name);

    /// <summary>How many names the recogniser listens for (more makes it slower and less sure).</summary>
    public const int Listened = 400;
    private const int MaxFiles = 3000;

    private static List<Document> _list = [];
    private static DateTime _read = DateTime.MinValue;
    private static int _reading;

    /// <summary>The documents found so far, newest first. A fresh look is started when the last one is two minutes old.</summary>
    public static List<Document> List()
    {
        if (DateTime.UtcNow - _read > TimeSpan.FromMinutes(2) && Interlocked.Exchange(ref _reading, 1) == 0)
            Task.Run(() =>
            {
                try { _list = Find(); _read = DateTime.UtcNow; }
                catch (Exception e) { Program.Log(e); _read = DateTime.UtcNow; }
                finally { _reading = 0; }
            });
        return _list;
    }

    /// <summary>The names to listen for after "open" and "attach".</summary>
    public static List<string> Names() => List().Select(d => d.Name).Distinct().Take(Listened).ToList();

    private static IReadOnlyList<(string Name, Document Item)> Pairs() => List().Select(d => (d.Name, d)).ToList();

    /// <summary>The document that fits a spoken name, or null.</summary>
    public static Document? Find(string spoken) => DocumentNames.Best(spoken, Pairs());

    /// <summary>"open budget": opens it in the program Windows uses for its kind. False when none fits.</summary>
    public static bool Open(string spoken)
    {
        if (Find(spoken) is not { } d) return false;
        try { Process.Start(new ProcessStartInfo(d.Path) { UseShellExecute = true }); return true; }
        catch (Exception) { return false; }
    }

    /// <summary>
    /// "attach budget and invoice": the files are put on the clipboard and pasted into the program in front (an email
    /// being written in Outlook, a WhatsApp chat…), which adds them as attachments. The clipboard is put back after.
    /// </summary>
    public static async Task<(List<Document> Attached, List<string> Missing)> AttachAsync(string spoken)
    {
        var (found, missing) = DocumentNames.Pick(spoken, Pairs());
        if (found.Count == 0) return (found, missing);
        var files = new StringCollection();
        files.AddRange(found.Select(d => d.Path).ToArray());
        if (!await Inserter.PasteFilesAsync(files)) return ([], missing);
        return (found, missing);
    }

    private static List<Document> Find()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var found = new List<(string Path, DateTime When)>();
        void Add(string path, DateTime when)
        {
            if (found.Count < MaxFiles && DocumentNames.Offered(path) && seen.Add(path)) found.Add((path, when));
        }
        // Recently used: Windows keeps a shortcut for each file opened.
        var recent = Environment.GetFolderPath(Environment.SpecialFolder.Recent);
        var shortcuts = new List<FileInfo>();
        try { shortcuts = new DirectoryInfo(recent).EnumerateFiles("*.lnk").OrderByDescending(f => f.LastWriteTimeUtc).Take(300).ToList(); }
        catch (Exception) { /* no recent list */ }
        var recentFound = new List<(string, DateTime)>();
        dynamic? shell = null;
        try { if (Type.GetTypeFromProgID("WScript.Shell") is { } type) shell = Activator.CreateInstance(type); }
        catch (Exception) { /* no shortcut reader */ }
        if (shell is not null)
            try
            {
                foreach (var lnk in shortcuts)
                    if (Target(shell, lnk.FullName) is string target && File.Exists(target)) recentFound.Add((target, lnk.LastWriteTimeUtc));
            }
            finally { System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shell); }
        // The folders documents are kept in, newest first. Recently used ones go first whatever their date.
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var folders = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            Path.Combine(profile, "Downloads"),
            Environment.GetEnvironmentVariable("OneDrive"),
            Environment.GetEnvironmentVariable("OneDriveCommercial"),
            Environment.GetEnvironmentVariable("OneDriveConsumer"),
        }.Where(f => !string.IsNullOrEmpty(f) && Directory.Exists(f)).Distinct(StringComparer.OrdinalIgnoreCase);
        var inFolders = new List<(string, DateTime)>();
        foreach (var folder in folders) Walk(folder!, 0, inFolders);
        foreach (var (p, w) in recentFound) Add(p, w);
        foreach (var (p, w) in inFolders.OrderByDescending(x => x.Item2)) Add(p, w);
        return found.Select(f => new Document(f.Path, DocumentNames.Of(f.Path))).Where(d => d.Name.Length > 0).ToList();
    }

    private static void Walk(string folder, int depth, List<(string, DateTime)> into)
    {
        if (into.Count >= MaxFiles * 2) return;
        try
        {
            var dir = new DirectoryInfo(folder);
            foreach (var f in dir.EnumerateFiles())
            {
                // Files kept only online (OneDrive's "available when online") are listed too: opening downloads them.
                if ((f.Attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0) continue;
                if (DocumentNames.Offered(f.Name)) into.Add((f.FullName, f.LastWriteTimeUtc));
            }
            if (depth >= 3) return;
            foreach (var d in dir.EnumerateDirectories())
            {
                if ((d.Attributes & (FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReparsePoint)) != 0) continue;
                if (d.Name.StartsWith('.') || d.Name is "node_modules" or "bin" or "obj" or "My Music" or "My Pictures" or "My Videos") continue;
                Walk(d.FullName, depth + 1, into);
            }
        }
        catch (Exception) { /* a folder that cannot be read */ }
    }

    /// <summary>The file a Windows shortcut points to, or null.</summary>
    private static string? Target(dynamic shell, string lnk)
    {
        try
        {
            dynamic shortcut = shell.CreateShortcut(lnk);
            string target = shortcut.TargetPath;
            return string.IsNullOrEmpty(target) ? null : target;
        }
        catch (Exception) { return null; }
    }
}
