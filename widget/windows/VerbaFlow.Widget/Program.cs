namespace VerbaFlow.Widget;

internal static class Program
{
    /// <summary>Where a problem that stops the Widget is written down, for whoever looks after it.</summary>
    public static readonly string ErrorLog = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VerbaFlow", "Widget", "widget-errors.txt");

    [STAThread]
    private static void Main()
    {
        // One Widget at a time for each person signed in to Windows.
        using var single = new Mutex(true, @"Local\VerbaFlow.Widget", out var first);
        if (!first) { MessageBox.Show("The VerbaFlow Widget is already running. Its icon is near the clock.", "VerbaFlow Widget"); return; }
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log(e.ExceptionObject as Exception);
        try
        {
            ApplicationConfiguration.Initialize();
            // Must come before any window or control exists (the synchronisation context below creates one).
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            // A slip in one dictation does not close the Widget; it is written down.
            Application.ThreadException += (_, e) => Log(e.Exception);
            SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
            var firstRun = !File.Exists(WidgetSettings.DefaultPath);
            Application.Run(new TrayApp(firstRun));
        }
        catch (Exception e)
        {
            Log(e);
            MessageBox.Show("The VerbaFlow Widget could not start. A note of what went wrong was saved in\n\n" + ErrorLog +
                "\n\nSend that file's text to whoever looks after VerbaFlow.", "VerbaFlow Widget", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    public static void Log(Exception? e)
    {
        if (e is null) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ErrorLog)!);
            File.AppendAllText(ErrorLog, $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz} Widget {typeof(Program).Assembly.GetName().Version}\r\n{e}\r\n\r\n");
        }
        catch (Exception) { /* nowhere to write */ }
    }
}
