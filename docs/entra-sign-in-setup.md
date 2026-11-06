# Sign-in with Microsoft work accounts (Microsoft Entra ID)

VerbaFlow lets people sign in with the Microsoft work account they use for Outlook and Teams. Microsoft 365 decides who can sign in and who is a VerbaFlow administrator. You set this up once. It takes about 10 minutes and needs an account that can register apps in your Microsoft 365 organisation (Global Administrator or Application Administrator).

Nothing here is a password or a key. The two IDs you copy are shown to anyone who opens the sign-in page.

## 1. Register VerbaFlow

1. Go to https://entra.microsoft.com and sign in with your admin account.
2. In the left menu, under **Entra ID**, click **App registrations**, then click **+ New registration** at the top. (Older layouts call this **Identity** › **Applications** › **App registrations**.)
3. Fill in:
   - **Name:** `VerbaFlow`
   - **Supported account types:** *Accounts in this organizational directory only (Single tenant)*
   - **Redirect URI:** choose **Single-page application (SPA)** and type `http://localhost:5044/signin`
4. Click **Register**.
5. On the page that opens (Overview), copy these two values into Notepad:
   - **Application (client) ID**
   - **Directory (tenant) ID**

## 2. Let VerbaFlow ask "who are you?"

1. In the left menu, click **Expose an API**.
2. Next to **Application ID URI**, click **Add**. Keep the suggested value (it starts with `api://`) and click **Save**.
3. Click **Add a scope** and fill in:
   - **Scope name:** `access_as_user`
   - **Who can consent?:** *Admins and users*
   - **Admin consent display name:** `Use VerbaFlow as you`
   - **Admin consent description:** `Lets VerbaFlow know who you are when you use it.`
   - **State:** *Enabled*
4. Click **Add scope**.

## 3. Create the administrator role

1. In the left menu, click **App roles**, then **Create app role**.
2. Fill in:
   - **Display name:** `VerbaFlow administrator`
   - **Allowed member types:** *Users/Groups*
   - **Value:** `Admin` (exactly this, with a capital A)
   - **Description:** `Can change vocabulary, saved text, the letterhead and Widget websites, and agree to reopen and delete requests.`
   - Tick **Do you want to enable this app role?**
3. Click **Apply**.

## 4. Allow the Widget to sign in

1. In the left menu, click **Authentication**.
2. Under **Single-page application**, click **Add URI** and type:
   `https://oeojnaneaakpjaemibkldnokbpgjmdnm.chromiumapp.org/`
3. Click **Save**.

That address belongs to the VerbaFlow Widget in Chrome and Edge. It is the same on every computer.

## 5. Agree on behalf of everyone (so nobody is asked)

1. In the left menu, click **Expose an API**.
2. Under **Authorized client applications**, click **+ Add a client application**.
3. In **Client ID**, paste VerbaFlow's own **Application (client) ID** (from the Overview page; it is the same code that follows `api://` in the Application ID URI).
4. Tick the scope ending in `access_as_user` and click **Add application**.

(Another way: **API permissions** › **Add a permission** › **My APIs** › **VerbaFlow** › tick **access_as_user**, then **Grant admin consent**. Microsoft sometimes shows "No results" under My APIs for a new app, so the way above is simpler.)

## 6. Choose the administrators

VerbaFlow needs **at least two** administrators, because reopening or deleting an approved item needs two different admins to agree.

1. In the left menu, under **Entra ID**, click **Enterprise apps** (older layouts: **Identity** › **Applications** › **Enterprise applications**) and click **VerbaFlow**.
2. Click **Users and groups**, then **Add user/group**.
3. Pick a person (yourself first), set **Select a role** to **VerbaFlow administrator**, and click **Assign**. Repeat for the second administrator.

A change here reaches VerbaFlow the next time that person signs in, or within an hour at most (when their sign-in is renewed).

**Optional, recommended:** to let only chosen people use VerbaFlow, open **Properties**, set **Assignment required?** to **Yes**, click **Save**, and add everyone who should use it under **Users and groups** (no role needed for ordinary users).

## 7. Tell VerbaFlow

Close any VerbaFlow window that is running. Open a Command Prompt in the VerbaFlow folder and type these, replacing the parts in angle brackets with the values from step 1 (both are on the app's **Overview** page):

```
git pull
cd src\VerbaFlow.Api
dotnet user-secrets set "Entra:TenantId" "<Directory (tenant) ID>"
dotnet user-secrets set "Entra:ClientId" "<Application (client) ID>"
cd ..\..
run.cmd
```

While it starts, look for the line beginning **Sign-in:** in the window. It should say `Sign-in: Microsoft work accounts (Entra ID, ...)`. Open http://localhost:5044 and click **Sign in with Microsoft**.

On your computer the test users (Alice, Bob, Carol, Dave, Sam) still work, so you can keep testing things that need several people. To switch them off: `dotnet user-secrets set "SignIn:TestUsers" "false"` in `src\VerbaFlow.Api`. They never work when VerbaFlow runs anywhere other than in Development.

## 8. The Widget

The Widget now has a fixed identity, which Microsoft needs, so it must be added again once:

1. Go to `chrome://extensions` (or `edge://extensions`), find **VerbaFlow Widget** and click **Remove**.
2. Click **Load unpacked** and choose the `VerbaFlow\widget\extension` folder again.
3. In the settings page that opens, click **Allow the microphone** (choose **Allow**), then **Sign in with Microsoft**, pick your language, and click **Save and check**.

The Widget stays signed in while the browser is open. After the browser restarts it signs in again by itself if you are signed in to Microsoft in that browser (for example Outlook on the web); otherwise the panel asks you to sign in from Settings.

## 9. The Widget for Windows (Outlook, Word, Teams…)

The Widget for Windows signs in through your normal browser, and Microsoft must know where to send you back afterwards. Once:

1. In the Entra admin center, open **App registrations** › **VerbaFlow** › **Authentication**.
2. Click **Add Redirect URI** (older layout: **Add a platform**) and choose **Mobile and desktop applications**.
3. Type `http://localhost` (exactly that, no port and no slash at the end) and click **Configure** or **Save**.
4. Check that the website's address under **Single-page application** is `http://localhost:5044/signin` (with `/signin`). Microsoft ignores the port of a `localhost` address, so a plain `http://localhost:5044/` clashes with `http://localhost` here: the web sign-in then loops back to "The Microsoft sign-in did not finish", and Support: errors shows `AADSTS9002326` (owner saw this, 2026-10-26).

Then install the Widget for Windows (`widget-windows.md`) and click **Sign in with Microsoft** in its settings.

## 10. The phone keyboards (Android and iPhone)

- **Android**: under **Mobile and desktop applications**, add `net.verbaflow.speak://auth` (exactly). See `widget-android.md`.
- **iPhone**: **Add a platform**, **iOS / macOS**, bundle ID `net.verbaflow.speak`. See `widget-iphone.md`.

## 11. Departments (who can see whose recordings)

People in the same department can open each other's recordings (read, listen, and export once approved), but not change them. VerbaFlow reads who is in which department from Microsoft groups. Do this once:

1. In the Entra admin center, open **App registrations** › **VerbaFlow** › **Token configuration**.
2. Click **+ Add groups claim**, tick **Security groups**, and click **Add**. (If your departments are Microsoft 365 groups, for example a Teams team, tick **All groups** instead.)
3. For each department, make sure there is a group with the right people in it: **Groups** › **All groups** › **+ New group**, Group type **Security**, a name such as "VerbaFlow Legal", then add the members.
4. Open the group and copy its **Object ID** from the Overview page.
5. In VerbaFlow, as an administrator, open **Departments** › **Add a department**, type the name and paste the Object ID. (Or wait until people have signed in: the page then lists the groups it has seen, with names of people in each, and a **Make a department** button.)
6. People are in the department from their next sign-in (Sign out, then Sign in with Microsoft). When someone is taken out of the group in Microsoft, they leave the department at their next sign-in.

Someone in more than about 200 groups gets no list from Microsoft; the Departments page says how many people this affects. The fix is step 2 with **Groups assigned to the application**, which needs Microsoft Entra ID P1, and then assigning the department groups under **Enterprise apps** › **VerbaFlow** › **Users and groups**.

The test users (Alice, Bob…) do not sign in with Microsoft, so on your computer an administrator can add them to a department by hand on the same page.

**Department admins.** Under each department an administrator can pick **Make someone a department admin…** (up to 10 per department; they do not have to be in it; anyone who has signed in at least once can be picked). A department admin can assign, re-assign and approve the recordings of the department's members, and export them, as an administrator can, but only those. Reopening, deleting, and changing settings stay with administrators. **take off** beside their name stops it. This is set in VerbaFlow, not in Microsoft.

## 12. Outlook drafts (Email… puts the file in your Drafts folder)

See `outlook-drafts.md`: one delegated permission (**Mail.ReadWrite**, with admin consent) and the client secret.

## 13. People using VerbaFlow on their own (personal Microsoft accounts)

So that students and others without a work account can use VerbaFlow, it can also accept personal Microsoft accounts (Outlook.com, Hotmail, Live, Microsoft 365 Family) and work or school accounts from **other** organisations (for example a university). Each of them gets a **personal VerbaFlow**: their recordings, vocabulary and saved text are theirs alone; your administrators cannot see them, and they cannot see your people, departments, letterhead or templates. Google accounts are set up separately (see `google-sign-in.md`).

1. In the Entra admin center, open **App registrations** › **VerbaFlow** › **Manifest**.
2. Find `"requestedAccessTokenVersion"` (inside `"api"`; in the older layout it is `"accessTokenAcceptedVersion"` at the top). Change its value from `null` to `2` and click **Save**. Microsoft requires this before personal accounts can be allowed.
3. Open **Authentication** (or **Branding & properties** in some layouts) and under **Supported account types** choose **Accounts in any organizational directory and personal Microsoft accounts**. Click **Save**.
4. Open **Expose an API**, click the `access_as_user` scope and make sure **Who can consent?** is **Admins and users** (a personal account has no administrator to agree for it). Click **Save**.
5. In the `src\VerbaFlow.Api` folder, run:

   ```
   dotnet user-secrets set "Entra:PersonalAccounts" "true"
   ```

6. Restart `run.cmd`. The start-up line `Sign-in:` now ends with "personal and other organisations' Microsoft accounts as personal accounts".

Your own people still sign in exactly as before and keep everything they had. Someone from another organisation is asked by Microsoft to agree that VerbaFlow may read their name and email; some universities switch that off, in which case their IT department must allow it, or the person uses a personal account instead.

## How it works (for whoever looks after VerbaFlow)

- The web app uses Microsoft's sign-in library (MSAL.js, `@azure/msal-browser`) and sends an access token for `api://<client id>/access_as_user` with every call. The Widget does the same through the browser's sign-in window (`chrome.identity.launchWebAuthFlow`, authorisation code with PKCE); its tokens are kept in browser-session memory only.
- The server (`SignIn.cs`) checks each token's signature against Microsoft, its audience, issuer and organisation (`tid`), and that it carries the `access_as_user` permission (so an app's own token is refused). A person is matched on their directory id (`oid`); the first sign-in creates the VerbaFlow user (History `user.first_sign_in`), and name, email and administrator status follow Microsoft 365 on every sign-in (History `user.admin_changed`).
- Administrator status comes only from the `Admin` app role. VerbaFlow support access never comes from a customer's directory (see `support-and-errors.md`).
- The Widget for Windows uses MSAL.NET with the system browser and a `http://localhost` (any port) redirect, as a public client; its sign-in is kept in `%LOCALAPPDATA%\VerbaFlow\Widget\signin.cache`, protected by Windows for that person only.
- With `Entra:PersonalAccounts=true` the web app signs in through Microsoft's `common` address and the server accepts tokens from any directory (issuer `https://login.microsoftonline.com/<tid>/v2.0`, still only for VerbaFlow's own `access_as_user`). A token from this organisation's directory is a member of the organisation as before; any other becomes a personal account matched on `microsoft:<tid>:<oid>` (`User.Individual`, `User.SignInId`), never an administrator.
- Live speech (Speech mode and the Widget) uses a one-minute ticket for its connection, obtained with the signed-in call, as before.

## Not done yet

- Items made under the test users stay with those test users; a Microsoft account is a new user.
- When VerbaFlow goes online, its web address must be added as another **Single-page application** redirect URI (step 4).
