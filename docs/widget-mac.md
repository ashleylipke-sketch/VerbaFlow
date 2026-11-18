# Widget for Mac: speaking into any Mac program

Built 2026-11-11 at the owner's request. **Not yet tried on a Mac.** It was written in the cloud session (which cannot run Mac programs) and compiled on GitHub's Macs, so whether it works is still to be seen: in particular the shortcut, typing into other programs, the microphone and the Microsoft sign-in.

It works like the Widget for Windows: click where the words should go in any program (Mail, Outlook, Word, Pages, WhatsApp, Slack, Notes…), press **⌥⇧D** (Option, Shift and D together) and speak.

## Install (once per Mac; again to update)
1. Microsoft sign-in only: add the Mac Widget to the app registration once (`entra-sign-in-setup.md`, step 10: **Add a platform**, **iOS / macOS**, bundle ID `net.verbaflow.widget`).
2. On the Mac, open GitHub in the browser (signed in), go to the VerbaFlow repository, **Releases**, **VerbaFlow Widget for Mac (newest)**, and download **VerbaFlow-Widget-mac.zip**.
3. Open the zip (double-click it in Downloads). Drag **VerbaFlow Widget** into **Applications**.
4. Open it from Applications. The first time, macOS refuses ("Apple cannot check it for malicious software" or "Apple could not verify…"), because it is not signed with an Apple Developer ID. Click **OK** (or **Done**), then use one of these (needed once per update):
   - **Easiest and surest:** open **Terminal** (Applications › Utilities), paste this line, press Return, then open the Widget again:
     ```
     xattr -dr com.apple.quarantine "/Applications/VerbaFlow Widget.app"
     ```
     (Nothing is printed when it works. It removes the "downloaded from the internet" mark that makes macOS refuse.)
   - Or open **System Settings**, **Privacy & Security**, scroll down to **"VerbaFlow Widget" was blocked…** and click **Open Anyway**, then **Open Anyway** again and your Mac password.
   - On macOS 14 or older, Control-click the Widget in Applications, choose **Open**, then **Open**.
5. A waveform icon appears in the menu bar (top right) and the settings window opens:
   - **VerbaFlow's address**: `http://localhost:5044` only works when VerbaFlow runs on the same Mac. If VerbaFlow runs on your Windows computer, the Mac needs that computer's address (see "VerbaFlow on another computer" below) or VerbaFlow online.
   - **Save and check**, then **Sign in with Microsoft** (Apple's sign-in window opens), or **Sign in through the VerbaFlow website** for a Google or email sign-in (agree to the code shown).
   - **The language you speak**.
   - **What macOS must allow**: **Allow the microphone**, and **Open Accessibility settings**, then switch on **VerbaFlow Widget** there (the Widget needs it to see where the cursor is and to type the words in).
   - Tick **Start the Widget when I log in to this Mac** if wanted.

The settings open again from the menu bar icon › **Settings and sign-in…**. **Quit the Widget** closes it.

### Building it on the Mac instead
With Apple's Command Line Tools (`xcode-select --install` in Terminal, once):
```
cd VerbaFlow/widget/mac
./make-app.sh
open build
```
then drag **VerbaFlow Widget** into Applications. A program built on the Mac itself opens without the "Open Anyway" step.

### VerbaFlow on another computer
`http://localhost:5044` means "this computer". A Mac reaching VerbaFlow on the Windows computer needs that computer's address on the home network and Windows' firewall to let it in; this has not been set up or tried. The simplest way is to wait until VerbaFlow is online (`going-online.md`), or run VerbaFlow on the Mac too.

## Using it
- Click where the words should go, press **⌥⇧D** and speak. The menu bar icon's **Speak here** does the same. (The shortcut can be changed in the settings, see below.)
- **Red** panel by the cursor: listening; words appear live (grey while still being heard). The program keeps the cursor. Press **⌥⇧D** again or **Stop**.
- **Amber**: the words in an editable box; **Accept** (⌘↩), **Speak more**, **Fix grammar** / **Undo fix**, **Cancel** (Esc).
- On Accept the words are pasted where the cursor was, as plain text (they take the formatting of that place). A space is added before them when the program tells macOS what is before the cursor and it is not a space.
- Select text first, then press ⌥⇧D, to replace it.
- Spoken commands, saved text, custom vocabulary, lists and "scratch that" work as in the other Widgets (the same live session as Speech mode).
- New paragraphs: **Automatic** leaves an empty line between paragraphs everywhere except Word and Pages; the settings can say always or never.
- In **Chrome and Edge**, ⌥⇧D is passed to the browser Widget (it knows the website, and blocks are per website). Untick this in the settings to use the Mac Widget there too. Safari has no VerbaFlow extension, so the Mac Widget works there.
- Never offered in password boxes (when macOS's secure typing is on, or the box says it is a password box).

## How the words get in
The Widget puts the text on the clipboard, brings the program back to the front, presses ⌘V, and a second later puts back what was on the clipboard. The text is marked as passing through, so clipboard managers that follow the usual convention (nspasteboard.org) do not keep it. If the program has quit by then, the words are left on the clipboard and a note says to paste them.

That works in any program you can type in. Where it does not, the amber panel has a line under the words, **Into <program>: Paste (⌘V)**; click it and choose another way. The choice is remembered for that program (by its bundle ID) and listed in the settings under **Programs with their own way**, where **Back to the usual way** forgets it.
- **Paste (⌘V)**: the usual way.
- **Type the letters**: the Widget types the characters as a keyboard would (any language; a new line presses Return). Use it for remote desktops and virtual machines, where a paste would use this Mac's clipboard. Microsoft Remote Desktop / Windows App, Citrix, VMware Fusion, Parallels, UTM, VNC Viewer, Screen Sharing, AnyDesk and TeamViewer use this unless changed. Careful in chat programs: Return may send the message.
- **Only copy (I paste myself)**: the words are copied and a note says to paste them.

A text box that says it cannot be typed in (its value cannot be set) gets the words copied with a note instead. Programs that say nothing are tried as usual.

## Keyboard shortcut
Settings → **Keyboard shortcut**: hold Option+Shift, Control+Shift, Control+Option, Control+Option+Shift, Option+Shift+Command or Control+Command, plus a letter, digit, F1 to F12 or Space. If another program already has the chosen shortcut, the settings say so and the old one keeps working. In Chrome and Edge the browser Widget keeps its own ⌥⇧D.

## What is kept
Nothing the person said. VerbaFlow keeps the facts of each use, as for the other Widgets, with the program's bundle ID (for example `com.microsoft.outlook`, `com.apple.mail`, `com.microsoft.word`) as the site and "Mac program" as the device. An administrator can switch the Widget off in a program by adding its bundle ID to the list on the Widget page. On the Mac: the settings in the Widget's preferences (address, test user, language, choices; no keys) and the sign-in in the login keychain (items "VerbaFlow").

## For whoever looks after it
- `widget/mac` is a Swift package (macOS 13 or newer): `WidgetMacCore` (paste text, paragraph gaps, site names, settings, ways of putting words in, shortcut rules; `swift test`) and the `VerbaFlowWidget` program (AppKit + SwiftUI): `AppDelegate` (menu bar item, `Hotkey` = Carbon `RegisterEventHotKey` with the chosen shortcut, browsers passed on with ⌥⇧D), `FocusTarget` (front program, caret and selection by the Accessibility API, `IsSecureEventInputEnabled` for passwords, `AXUIElementIsAttributeSettable` for read-only boxes), `DictationPanel` (non-activating floating panel), `Controller` (one dictation at a time), `Microphone` (AVAudioEngine, 16 kHz mono), `Inserter` (clipboard and ⌘V by CGEvent, or typing by `keyboardSetUnicodeString` 16 characters at a time, or copy only), `SettingsWindow` (start at login by `SMAppService`). `Api`, `LiveListener` and `SignIn` are the iPhone app's, adapted; paragraphs come from `widget/iphone/Shared`.
- `make-app.sh` builds `build/VerbaFlow Widget.app` (Info.plist in `Resources/`, bundle ID `net.verbaflow.widget`, menu bar only, icon from `Resources/icon-1024.png`), signed ad hoc. GitHub workflow **Mac Widget** (`.github/workflows/mac.yml`, macos-15) runs the tests, builds for Apple silicon and Intel, and from main puts `VerbaFlow-Widget-mac.zip` on the Releases page as `mac-latest`.
- Ad-hoc signing means macOS may ask for the Accessibility permission again after each update, and the keychain may ask once whether the Widget may read its sign-in ("Always Allow"). An Apple Developer ID (about $99 a year) and notarisation would remove both and the "Open Anyway" step.

## Not built
A microphone picker (it uses the input chosen in System Settings, Sound), Developer ID signing and notarisation, noticing by itself that a paste did not land.
