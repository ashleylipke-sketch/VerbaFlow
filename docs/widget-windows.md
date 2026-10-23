# Widget for Windows: speaking into any Windows program

Built 2026-10-23 at the owner's request (desktop Outlook, Word, Teams…). **Not yet tried by the owner. It could not be run or tested on Windows where it was built**: its own logic (the connection to VerbaFlow, the live text, reconnecting, how the words are put together) is tested against the real VerbaFlow server with the stand-in speech service, but the window, the shortcut, the microphone, the paste into programs and the Microsoft sign-in have only been built, not run. Untested on real Azure.

## Install (once per computer; again to update)
1. Microsoft sign-in only: add the `http://localhost` redirect once (`entra-sign-in-setup.md`, step 9).
2. In the VerbaFlow folder, double-click **`widget-windows.cmd`** (or run it from a Command Prompt). It builds the Widget into `%LOCALAPPDATA%\VerbaFlow\Widget\app`, adds **VerbaFlow Widget** to the Start menu and starts it. It needs the .NET 10 SDK (already there for VerbaFlow).
3. The settings window opens the first time: check the VerbaFlow address (`http://localhost:5044` on the owner's computer; VerbaFlow must be running), **Save and check**, then **Sign in with Microsoft** (the browser opens Microsoft's page), choose the language, and tick **Start the Widget when I sign in to Windows** if wanted.

The settings open again from the VerbaFlow icon near the clock (click it, or right-click › **Settings and sign-in…**). Right-click › **Quit the Widget** closes it.

## Using it
- Click where the words should go in any program, press **Alt+Shift+D** and speak.
- **Red** panel beside the cursor: listening; words appear live (grey while still being heard). The program keeps the cursor. Press **Alt+Shift+D** again or **Stop**.
- **Amber**: the words in an editable box; **Accept** (Ctrl+Enter), **Speak more**, **Fix grammar** / **Undo fix**, **Cancel** (Escape).
- On Accept the panel closes and the words are pasted where the cursor was, as plain text (they take the formatting of that place, like Word's "Keep text only"). A space is added before them when the program tells Windows what is before the cursor and it is not a space.
- Select text first, then press Alt+Shift+D, to replace it: the selected words appear in the panel, and Accept replaces them.
- Spoken commands, saved text, custom vocabulary, lists and "scratch that" work as in the browser Widget (the same live session as Speech mode).
- New paragraphs: **Automatic** leaves an empty line between paragraphs everywhere except Word (whose paragraphs have their own space); the settings can say always or never.
- In **Chrome and Edge**, Alt+Shift+D is passed to the browser Widget (it knows the website, and blocks are per website). Untick this in the settings to use the Windows Widget there too.
- Never offered in password boxes, when the program tells Windows it is one (UI Automation `IsPassword`).

## How the words get in
The Widget puts the text on the clipboard, presses Ctrl+V in the program, and a second later puts back what was on the clipboard (text, formatted text, pictures and files). The Widget's text is marked so that Windows keeps it out of clipboard history and the cloud clipboard. If the program's window has closed by then, the words are left on the clipboard and a note says to paste them.

## What is kept
Nothing the person said. Exactly as for the browser Widget: VerbaFlow keeps the facts of each use (`doc_widget_sessions`, audit `widget.*`), with the program's file name (for example `outlook.exe`, `winword.exe`, `olk.exe` for the new Outlook, `ms-teams.exe`) as the site and "Windows program" as the device. An administrator can switch the Widget off in a program by adding its file name (for example `teams.exe`) to the list on the Widget page. On the computer: the settings in `%APPDATA%\VerbaFlow\Widget\settings.json` (address, test user, language, choices; no keys) and the Microsoft sign-in in `%LOCALAPPDATA%\VerbaFlow\Widget\signin.cache`, protected by Windows for that person only.

## For whoever looks after it
- `widget/windows/VerbaFlow.Widget.Core` (net10.0, no Windows parts, tested in `tests/VerbaFlow.Tests/WidgetWindows`): `WidgetSettings`, `VerbaFlowClient` (the same calls as the extension: `/api/sign-in`, `/api/widget/*`, `/api/live/{id}/ticket`, `/api/languages`, `/api/me`), `LiveListener` (WebSocket `/api/live/{id}/socket`, 16 kHz 16-bit mono PCM; sound kept in memory while listening and sent again from `finalToMs` after a lost connection, retrying after 1, 2, 4, 8, then every 10 s; `{"type":"end"}` to finish, 15 s to give up), `DictationText` (paragraphs and list markers as `content.js`, joining, spacing, paste text).
- `widget/windows/VerbaFlow.Widget` (net10.0-windows, WinForms, UI Automation from WPF): `TrayApp` (tray icon, `RegisterHotKey` Alt+Shift+D, browsers forwarded), `FocusTarget` (foreground window, program, caret by `GetGUIThreadInfo` or the focused element's box, password, selection and the character before the cursor by UI Automation, given at most 1.5 s), `DictationPanel`, `WidgetController` (one dictation at a time; nothing kept after Accept or Cancel), `Microphone` (NAudio.WinMM 2.2.1, MIT; Windows' default recording device at 16 kHz mono), `Inserter` (clipboard paste with `ExcludeClipboardContentFromMonitorProcessing` and `CanIncludeInClipboardHistory`=0), `WindowsSignIn` (MSAL.NET 4.90.1 with Microsoft.Identity.Client.Extensions.Msal, MIT; system browser, `http://localhost` redirect; test user from the settings when VerbaFlow allows test users and nobody signed in), `SettingsForm` (start with Windows = `HKCU\...\Run`).
- One copy per Windows user (a named mutex). Framework-dependent build for the computer's own processor.

## Not built
A choice of shortcut, a microphone picker (it uses Windows' default recording device), a sound level meter, a signed installer, admin-pushed install, typing the words directly instead of pasting (some programs may refuse a paste), Mac.
