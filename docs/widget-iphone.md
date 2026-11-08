# VerbaFlow keyboard for iPhone and iPad

The Widget for iPhone is a **keyboard**. In any app (Mail, Outlook, WhatsApp, Notes), you switch to the VerbaFlow
keyboard, tap the microphone and speak. The words show in the keyboard as they are heard. When you stop, you can
**Accept** (they are typed in), **Speak more**, **Fix grammar** or **Cancel**. Spoken commands, saved text ("insert
disclaimer 1.1") and voice corrections ("scratch that") work as in the other Widgets.

## How it works, and why the app opens

Apple does not let a keyboard use the microphone. So the **VerbaFlow app** does the listening:

1. The first time you tap the microphone, the VerbaFlow app opens for a moment and starts listening.
2. Go back to the app you were typing in: tap **◀** at the top left of the screen (or swipe along the bottom edge).
   The words appear in the VerbaFlow keyboard there.
3. For **five minutes** after that, the app stays ready in the background (the orange dot at the top of the screen
   shows the microphone is ready), and the keyboard starts listening straight away without opening the app.

Nothing you say is kept: as with the other Widgets, VerbaFlow's history records only counts (how long, how many words),
and admins can switch the keyboard off for everyone (it shows as "iphone" or "ipad" on the Widget page).
iPhone does not tell a keyboard which app it is typing into, so admins cannot block it per app.

## What is needed before you can install it

1. **An Apple Developer account** (about $99 a year): https://developer.apple.com/programs/enroll/ . Enrol as an
   individual or as your organisation. Apple can take a day or two to approve it.
2. **VerbaFlow online**, at an https address (see `docs/going-online.md`). The phone link from `phone.cmd` works only in
   a browser, not in an app.
3. **Microsoft sign-in for the app** (once): in the Azure portal, open **App registrations**, **VerbaFlow**,
   **Authentication**, **Add a platform**, **iOS / macOS**, and type the bundle ID `net.verbaflow.speak`. Save. This adds
   the redirect `msauth.net.verbaflow.speak://auth`.

## Getting it onto your iPhone (TestFlight)

GitHub builds the app on a Mac for you, so you do not need one.

1. In **App Store Connect** (https://appstoreconnect.apple.com), **Apps**, **+**, **New App**: platform iOS, name
   VerbaFlow (or another free name), language English (UK), bundle ID: choose **net.verbaflow.speak** (if it is not in
   the list, first add it at https://developer.apple.com/account/resources/identifiers with the App Groups capability,
   and add the group `group.net.verbaflow.speak`; do the same for `net.verbaflow.speak.keyboard`). SKU: verbaflow.
2. In App Store Connect, **Users and Access**, **Integrations**, **App Store Connect API**, **Generate API Key**, name
   "GitHub", access **App Manager**. Download the key file (it downloads only once). Note the **Key ID** and the
   **Issuer ID** shown on that page.
3. On GitHub, open the VerbaFlow repository, **Settings**, **Secrets and variables**, **Actions**, **New repository
   secret**, and add four secrets (type them there, never in a chat):
   - `APPLE_TEAM_ID`: your Team ID (developer.apple.com, **Account**, **Membership details**).
   - `APP_STORE_KEY_ID`: the Key ID.
   - `APP_STORE_ISSUER_ID`: the Issuer ID.
   - `APP_STORE_KEY_P8`: open the downloaded `.p8` file in Notepad and paste all of it.
4. On GitHub, **Actions**, **iPhone app**, **Run workflow**, tick **Send to TestFlight**, **Run workflow**. It takes
   about 10 to 15 minutes. Apple then checks the build (usually under an hour).
5. On your iPhone, install **TestFlight** from the App Store. In App Store Connect, **TestFlight**, add yourself as an
   internal tester; the email invitation opens TestFlight and installs VerbaFlow.

## Switching it on

1. Open the **VerbaFlow** app: type VerbaFlow's address, **Sign in with Microsoft**, and pick your language.
2. **Settings**, **General**, **Keyboard**, **Keyboards**, **Add New Keyboard**, **VerbaFlow**. Then tap **VerbaFlow** in
   that list and turn on **Allow Full Access** (the keyboard needs it to hear from the VerbaFlow app; it sends nothing
   anywhere itself).
3. In any app, hold the **🌐** key and choose **VerbaFlow**.

## For developers

- `widget/iphone/project.yml` (XcodeGen), `App/` (SwiftUI app: sign-in with PKCE in `ASWebAuthenticationSession`,
  tokens in the keychain; `Microphone` 16 kHz 16-bit mono; `LiveListener` the same live connection and reconnect rules
  as the Windows Widget; `Controller` handles the keyboard's requests), `Keyboard/` (UIKit `UIInputViewController`),
  `Shared/` (Swift package: `DictationText` ported from the other Widgets, `Link`: status and requests in the App Group
  `group.net.verbaflow.speak` plus Darwin notifications, polled every 0.25 s as well).
- Server: no changes; the keyboard uses `/api/widget/*` and `/api/live/*` like the other Widgets (site `iphone`/`ipad`,
  device "iPhone keyboard"/"iPad keyboard").
- `.github/workflows/iphone.yml`: shared tests (`swift test`), a simulator build without signing on every push to
  `widget/iphone`, and (Run workflow, Send to TestFlight) archive, automatic signing with the App Store Connect API key,
  and upload.
- Opening the app from the keyboard goes up the responder chain to `UIApplication` (a keyboard may not open apps
  itself); Apple could stop that in a future iOS version.

Google and other personal accounts: **Google or another personal account** in the app (see "Personal accounts" in `widget.md`).
