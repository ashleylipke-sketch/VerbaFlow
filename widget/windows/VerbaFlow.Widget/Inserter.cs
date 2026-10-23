namespace VerbaFlow.Widget;

/// <summary>
/// Puts the accepted text into the program as a paste of plain text, which every Windows program understands and which
/// takes the formatting of the place it lands (as "Keep text only" does in Word). The clipboard is put back as it was
/// straight after, and the Widget's text is marked so that Windows keeps it out of clipboard history and cloud clipboard.
/// </summary>
internal static class Inserter
{
    /// <summary>Must be called on the window (STA) thread. False when the window has gone.</summary>
    public static async Task<bool> PasteAsync(FocusTarget target, string text)
    {
        if (!Native.IsWindow(target.Window)) return false;
        var saved = Save();
        var data = new DataObject();
        data.SetData(DataFormats.UnicodeText, text);
        data.SetData("ExcludeClipboardContentFromMonitorProcessing", new MemoryStream([0]));
        data.SetData("CanIncludeInClipboardHistory", new MemoryStream(new byte[4]));
        data.SetData("CanUploadToCloudClipboard", new MemoryStream(new byte[4]));
        try { Clipboard.SetDataObject(data, false, 10, 50); }
        catch (System.Runtime.InteropServices.ExternalException) { return false; }

        Native.SetForegroundWindow(target.Window);
        await Task.Delay(150); // the program becomes active and puts its cursor back
        Native.ReleaseModifiers();
        Native.Press(Native.VK_CONTROL, Native.VK_V);
        // The program reads the clipboard when it handles the paste; slow programs get a second before it is put back.
        await Task.Delay(1000);
        Restore(saved);
        return true;
    }

    /// <summary>The clipboard's text, pictures and files, to put back after the paste.</summary>
    private static DataObject? Save()
    {
        try
        {
            var current = Clipboard.GetDataObject();
            if (current is null) return null;
            var copy = new DataObject();
            var any = false;
            foreach (var f in new[] { DataFormats.UnicodeText, DataFormats.Text, DataFormats.Rtf, DataFormats.Html, DataFormats.CommaSeparatedValue, DataFormats.FileDrop, DataFormats.Bitmap })
            {
                try
                {
                    if (!current.GetDataPresent(f, false)) continue;
                    var d = current.GetData(f, false);
                    if (d is null) continue;
                    copy.SetData(f, d);
                    any = true;
                }
                catch (Exception) { /* a format the other program will not give */ }
            }
            return any ? copy : null;
        }
        catch (Exception) { return null; }
    }

    private static void Restore(DataObject? saved)
    {
        try
        {
            if (saved is null) Clipboard.Clear();
            else Clipboard.SetDataObject(saved, true, 10, 50);
        }
        catch (Exception) { /* another program holds the clipboard: it keeps the dictated text */ }
    }

    /// <summary>The text copied for the person to paste themselves (when the window has gone).</summary>
    public static void Copy(string text)
    {
        try { Clipboard.SetDataObject(new DataObject(DataFormats.UnicodeText, text), true, 10, 50); } catch (Exception) { }
    }
}
