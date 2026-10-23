namespace VerbaFlow.Widget;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        // One Widget at a time for each person signed in to Windows.
        using var single = new Mutex(true, @"Local\VerbaFlow.Widget", out var first);
        if (!first) { MessageBox.Show("The VerbaFlow Widget is already running. Its icon is near the clock.", "VerbaFlow Widget"); return; }
        ApplicationConfiguration.Initialize();
        SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, _) => { /* a slip in one dictation does not close the Widget */ };
        var firstRun = !File.Exists(WidgetSettings.DefaultPath);
        Application.Run(new TrayApp(firstRun));
    }
}
