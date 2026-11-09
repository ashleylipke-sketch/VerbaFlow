# Sign-in with Google (personal accounts)

For people using VerbaFlow on their own, such as students or anyone without a work account. Someone who signs in with Google gets a **personal VerbaFlow**: their recordings, vocabulary and saved text are theirs alone; the organisation's administrators cannot see them, and they cannot see the organisation's people, departments, letterhead or minutes templates. The online-meeting note-taker is not offered to personal accounts.

You need a Google account. Nothing here costs money.

## 1. Make a Google Cloud project (skip if you already have one)

1. Go to https://console.cloud.google.com and sign in.
2. At the top, click the project picker, then **New project**. Name it `VerbaFlow` and click **Create**. Make sure it is selected at the top.

## 2. The consent screen (what people see when they sign in)

1. In the search box at the top, type **Google Auth Platform** and open it (older layout: **APIs & Services** › **OAuth consent screen**).
2. Click **Get started**. App name `VerbaFlow`, your email as the support email, **Next**.
3. Audience: **External**, **Next**. Contact email: yours, **Next**. Agree, **Create**.
4. Under **Audience**, while it says **Testing**, only the people you add under **Test users** can sign in. Add your own Gmail address there to try it. When others should use it, click **Publish app** (signing in with name and email needs no review by Google).

## 3. The client id

1. Open **Clients** (older layout: **APIs & Services** › **Credentials** › **Create credentials** › **OAuth client ID**) and click **Create client**.
2. Application type: **Web application**. Name: `VerbaFlow web`.
3. Under **Authorised JavaScript origins**, click **Add URI** and type `http://localhost:5044`. (When VerbaFlow goes online, add its address too. A phone tunnel address from `phone.cmd` must be added the same way.)
4. Leave **Authorised redirect URIs** empty. Click **Create**.
5. Copy the **Client ID** (it ends in `.apps.googleusercontent.com`). It is not a secret, but keep it out of GitHub like the Microsoft ids. You do not need the client secret.

## 4. Tell VerbaFlow

In the `src\VerbaFlow.Api` folder, run (paste your own client id):

```
dotnet user-secrets set "Google:ClientId" "123456789-abc.apps.googleusercontent.com"
```

Restart `run.cmd`. The start-up line `Sign-in:` now mentions "Google accounts as personal accounts", and the sign-in page has a **Sign in with Google** button.

## 5. Gmail drafts (optional)

With Google set up, **Export ▾ → Email…** also offers **Put it in my Gmail drafts** to everyone (a personal account, or someone at work whose email is Gmail). The first time, Google's window asks the person to allow VerbaFlow to "manage drafts and send emails"; VerbaFlow only ever makes the draft they asked for, and keeps no copy of the permission. To switch it on:

1. In Google Cloud, search for **Gmail API** and click **Enable**.
2. Open **Google Auth Platform** › **Data access**, click **Add or remove scopes**, tick `.../auth/gmail.compose` (or paste `https://www.googleapis.com/auth/gmail.compose` under **Manually add scopes**), click **Update**, then **Save**.
3. While the app is in **Testing**, only the test users you added in step 2 can use Gmail drafts (up to 100 people). That is enough to try it.

**Before others outside your test users can use Gmail drafts:** Google counts `gmail.compose` as a *restricted* permission. Publishing an app that uses it needs Google's verification, including a yearly security assessment by an approved assessor (this usually costs money). Signing in with Google does not need any of that. Until then, everyone else can use **Or download it as an email file**, which works with Gmail too.

## How it works (for whoever looks after VerbaFlow)

- The browser uses Google's own button (Google Identity Services, `https://accounts.google.com/gsi/client`) and gets a Google ID token. `POST /api/sign-in/google` checks it once (Google's signature, issuer `accounts.google.com`, audience = `Google:ClientId`, not expired; `PersonalSignIn.cs`, `GoogleSignIn`) and matches the person on `google:<sub>`.
- VerbaFlow then gives its own sign-in, a `vf1.` value made with ASP.NET Data Protection (keys in `data/keys/sign-in`, never committed). It lasts 30 days, is kept in the browser (`localStorage` `vfToken`) and is swapped for a fresh one each time VerbaFlow opens (`POST /api/sign-in/renew`), so someone who uses VerbaFlow now and then stays signed in. Sign out removes it.
- If the keys in `data/keys/sign-in` are lost, everyone signed in with Google simply signs in again.
- Gmail drafts: the browser asks Google for `gmail.compose` with Google's token client (`gmailMailToken` in `web/src/auth.ts`), sends it once to `POST /api/items/{id}/export/own-draft` {service: gmail, mailToken}, and the server uploads the email (`EmlWriter`) to `gmail/v1/users/me/drafts` (`Mail/OwnMailDrafts.cs`). History `item.gmail_draft` / `gmail_draft.failed`; the permission is never stored or logged.
- The Widgets (browser extension, Windows, phones) link to a Google account through the website: see "Personal accounts" in `widget.md`.
- People without a Google or Microsoft account can sign in with an emailed code instead: see `email-sign-in.md`.
