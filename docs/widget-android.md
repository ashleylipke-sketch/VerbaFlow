# VerbaFlow keyboard for Android phones and tablets

The Widget for Android is a **keyboard**. In any app (Gmail, Outlook, WhatsApp, Messages, Teams), you switch to the
VerbaFlow keyboard, tap **Speak** and talk. The words show in the keyboard as they are heard. Tap **Stop**, then
**Accept** (they are typed into the box), **Speak more**, **Fix grammar** or **Cancel**. Spoken commands, saved text
("insert disclaimer 1.1") and voice corrections ("scratch that") work as in the other Widgets.

Unlike iPhone, Android lets a keyboard use the microphone itself, so no other app opens while you dictate.

The keyboard also has 🌐 (next keyboard; hold it to choose one), ⚙ (opens the VerbaFlow app), space, ⌫ and return,
so small fixes can be made without switching back. It is not a full typing keyboard: switch with 🌐 to type.

Nothing you say is kept: VerbaFlow's history records only counts (how long, how many words) and which app it was used
in (for example `com.whatsapp`). Admins can switch it off for an app on the Widget page by typing that name.
The keyboard never listens in password boxes, and stops listening as soon as it is put away.

## What is needed

1. **An Android phone or tablet** (Android 8 or newer).
2. **VerbaFlow online**, at an https address (see `docs/going-online.md`). The phone link from `phone.cmd` works only
   in a browser, not in an app.
3. **Microsoft sign-in for the app** (once): in the Entra admin center, **App registrations**, **VerbaFlow**,
   **Authentication**, **Add Redirect URI** (older layout: **Add a platform**), **Mobile and desktop applications**,
   type `net.verbaflow.speak://auth` exactly, and save.

No Google developer account is needed: the app is installed straight from GitHub, not from the Play Store.

## Installing it

GitHub builds the app each time it changes.

1. On the Android phone, open the GitHub app or https://github.com in the browser and sign in.
2. Open the **VerbaFlow** repository, **Releases**, **VerbaFlow keyboard for Android (newest)**, and tap
   **VerbaFlow-keyboard.apk**.
3. Android asks whether the browser may install apps: tap **Settings**, turn on **Allow from this source**, go back,
   and tap **Install**. (Play Protect may say it does not know the app: tap **More details**, **Install anyway**.)
4. To update later, do the same: the new version installs over the old one and keeps your settings.

## Switching it on

1. Open the **VerbaFlow** app.
2. Type VerbaFlow's address and tap **Save address**.
3. Tap **Sign in with Microsoft** (or, on the owner's own computer only, the test user is used).
4. Pick the language you speak.
5. Tap **Allow the microphone** and choose **While using the app**.
6. Tap **Open keyboard settings**, turn on **VerbaFlow**, and accept Android's warning (every keyboard gets it; the
   VerbaFlow keyboard sends what you say only to your VerbaFlow).
7. Try it in the box at the bottom: tap the box, tap the keyboard button at the bottom of the screen (or 🌐 on your
   usual keyboard) and choose **VerbaFlow**, tap **Speak** and talk.

## For developers

- `widget/android`: Kotlin, plain Android views, OkHttp 4.12 (calls and the live WebSocket), coroutines.
  `VerbaFlowIme` (the keyboard, `InputMethodService`), `MainActivity` (settings, sign-in with PKCE in the browser,
  redirect `net.verbaflow.speak://auth`), `LiveListener` (same live connection and reconnect rules as the Windows
  Widget), `Microphone` (`AudioRecord`, 16 kHz 16-bit mono, voice-recognition source), `DictationText` (ported from
  the other Widgets, unit-tested), `Prefs` (app-private storage, not backed up; tokens kept there).
- Server: no changes. Site = the app's package name (`_` becomes `-`), device "Android keyboard".
- `.github/workflows/android.yml`: unit tests and a debug build on each push to `widget/android`; the app file is kept
  as a run artifact, and builds from `main` replace the `android-latest` release. The debug signing key is kept in the
  Actions cache so updates install over each other; if that cache is ever dropped (unused for 7 days), the next build
  has a new key and the old app must be removed before installing it.
