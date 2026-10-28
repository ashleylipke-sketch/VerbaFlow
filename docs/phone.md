# Dictating on a phone

VerbaFlow's pages work on a phone screen: the page list folds behind a **Menu** button, lists show each recording as a small card, and Dictate and Speech can be used with taps.

## Opening VerbaFlow on your phone (while it runs on your computer)

A phone browser only lets a web page use the microphone over a secure (https) address. `phone.cmd` gives VerbaFlow such an address with Microsoft Dev Tunnels. The address is **private**: only the Microsoft account that created it can open it.

1. Start VerbaFlow as usual with `run.cmd` and leave that window open.
2. Double-click `phone.cmd` in the VerbaFlow folder (or run it from a Command Prompt).
   - The first time it installs Dev Tunnels and asks you to open a new Command Prompt and run it again.
   - It then opens a browser to sign in with your Microsoft account (one time).
3. It shows a line "Connect via browser: https://…devtunnels.ms". Open that address on your phone, sign in with the same Microsoft account, choose **Continue**.
4. Pick a test user (or sign in with Microsoft, see below) and choose Dictate or Speech.

The address stays the same for 30 days (it is kept in `phone-tunnel.txt`, not in GitHub); after that `phone.cmd` makes a new one. Press Ctrl+C in the `phone.cmd` window to close it. Your computer must stay on and awake while you use it.

**Microsoft sign-in on the phone** needs the address added once to the app registration: in the Azure portal, open App registrations → VerbaFlow → Authentication → Single-page application → Add URI, and add the phone address followed by `/signin` (for example `https://verbaflow-123456-5044.euw.devtunnels.ms/signin`). Test users work without this, because only you can open the address.

Once VerbaFlow is online (see `going-online.md`) none of this is needed: open its online address on the phone.

## While recording on a phone

- Keep VerbaFlow on screen. VerbaFlow asks the phone to keep the screen on while recording (on iPhones it also plays a tiny silent, invisible video, which is what keeps them awake). In battery saving (Low Power) mode a phone may still lock.
- If the screen locks, you switch app, or a phone call takes the microphone, recording stops and a message at the top of the page says so. Everything recorded up to then is kept; choose what to do next as after Stop.
- On the sound wave, tap to put the cursor; use **Delete…** and **Overwrite…** (tap where the part starts, then where it ends) to mark a part.

## Not yet checked

Checked in a desktop browser made to act as a phone (Chromium, iPhone-size screen, touch, a fake microphone). **Not yet tried on a real iPhone or Android phone, and Dev Tunnels has not been tried with VerbaFlow.** Safari on iPhone may behave differently with recording; please report anything odd.
