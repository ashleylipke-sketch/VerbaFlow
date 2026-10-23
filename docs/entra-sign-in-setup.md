# Sign-in with Microsoft work accounts (Microsoft Entra ID)

VerbaFlow lets people sign in with the Microsoft work account they use for Outlook and Teams. Microsoft 365 decides who can sign in and who is a VerbaFlow administrator. You set this up once. It takes about 10 minutes and needs an account that can register apps in your Microsoft 365 organisation (Global Administrator or Application Administrator).

Nothing here is a password or a key. The two IDs you copy are shown to anyone who opens the sign-in page.

## 1. Register VerbaFlow

1. Go to https://entra.microsoft.com and sign in with your admin account.
2. In the left menu, under **Entra ID**, click **App registrations**, then click **+ New registration** at the top. (Older layouts call this **Identity** › **Applications** › **App registrations**.)
3. Fill in:
   - **Name:** `VerbaFlow`
   - **Supported account types:** *Accounts in this organizational directory only (Single tenant)*
   - **Redirect URI:** choose **Single-page application (SPA)** and type `http://localhost:5044/`
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

Open a Command Prompt in the VerbaFlow folder and type these, replacing the parts in angle brackets with the values from step 1:

```
cd src\VerbaFlow.Api
dotnet user-secrets set "Entra:TenantId" "<Directory (tenant) ID>"
dotnet user-secrets set "Entra:ClientId" "<Application (client) ID>"
cd ..\..
run.cmd
```

When it starts, the window should say: `Sign-in: Microsoft work accounts (Entra ID, ...)`. Open http://localhost:5044 and click **Sign in with Microsoft**.

On your computer the test users (Alice, Bob, Carol, Dave, Sam) still work, so you can keep testing things that need several people. To switch them off: `dotnet user-secrets set "SignIn:TestUsers" "false"` in `src\VerbaFlow.Api`. They never work when VerbaFlow runs anywhere other than in Development.

## 8. The Widget

The Widget now has a fixed identity, which Microsoft needs, so it must be added again once:

1. Go to `chrome://extensions` (or `edge://extensions`), find **VerbaFlow Widget** and click **Remove**.
2. Click **Load unpacked** and choose the `VerbaFlow\widget\extension` folder again.
3. In the settings page that opens, click **Allow the microphone** (choose **Allow**), then **Sign in with Microsoft**, pick your language, and click **Save and check**.

The Widget stays signed in while the browser is open. After the browser restarts it signs in again by itself if you are signed in to Microsoft in that browser (for example Outlook on the web); otherwise the panel asks you to sign in from Settings.

## How it works (for whoever looks after VerbaFlow)

- The web app uses Microsoft's sign-in library (MSAL.js, `@azure/msal-browser`) and sends an access token for `api://<client id>/access_as_user` with every call. The Widget does the same through the browser's sign-in window (`chrome.identity.launchWebAuthFlow`, authorisation code with PKCE); its tokens are kept in browser-session memory only.
- The server (`SignIn.cs`) checks each token's signature against Microsoft, its audience, issuer and organisation (`tid`), and that it carries the `access_as_user` permission (so an app's own token is refused). A person is matched on their directory id (`oid`); the first sign-in creates the VerbaFlow user (History `user.first_sign_in`), and name, email and administrator status follow Microsoft 365 on every sign-in (History `user.admin_changed`).
- Administrator status comes only from the `Admin` app role. VerbaFlow support access never comes from a customer's directory (see `support-and-errors.md`).
- Live speech (Speech mode and the Widget) uses a one-minute ticket for its connection, obtained with the signed-in call, as before.

## Not done yet

- Departments from Microsoft 365 groups (decision 02).
- Items made under the test users stay with those test users; a Microsoft account is a new user.
- Email drafts through Microsoft Graph (Outlook) instead of a downloaded .eml file.
- When VerbaFlow goes online, its web address must be added as another **Single-page application** redirect URI (step 4).
