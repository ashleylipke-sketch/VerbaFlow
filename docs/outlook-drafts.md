# Outlook drafts

When you choose **Export ▾ → Email… → Put it in my Outlook drafts**, VerbaFlow puts a new email in your own Outlook **Drafts** folder, with the PDF or Word file attached. Open it in Outlook (desktop, web or phone), choose who it goes to, and send it. Because it is a real draft in your mailbox, the attachment opens normally. That fixes the problem new Outlook has with files in a downloaded email.

**Or download it as an email file** still works as before.

## What it needs
- **A Microsoft sign-in.** The button only shows for people signed in with their Microsoft work account, not for the test users (Alice, Bob…).
- **A Microsoft 365 (Exchange Online) mailbox for that account.** A Microsoft account whose Outlook only shows a Gmail or other account has no mailbox VerbaFlow can reach. In that case VerbaFlow says so in a plain sentence with a VF- reference, and you can use the email file instead.
- **VerbaFlow's client secret and one permission**, set up once by an administrator (below).

## Setting it up (once, by an administrator)
1. Go to **entra.microsoft.com** › **App registrations** › open **VerbaFlow**.
2. **API permissions** › **Add a permission** › **Microsoft Graph** › **Delegated permissions**. Tick **Mail.ReadWrite**, click **Add permissions**, then click **Grant admin consent** (and **Yes**).
3. If you already set `Entra:ClientSecret` for Microsoft 365 calendars (see `meet-setup.md`), skip this step. Otherwise:
   - Go to **Certificates & secrets** › **New client secret**, give it a name such as `VerbaFlow`, choose 24 months, and click **Add**.
   - Copy the **Value** straight away; it is only shown once. Do not paste it into a chat or an email.
   - Then, in Command Prompt in the VerbaFlow folder, run these lines, pasting the value yourself:
     ```
     cd src\VerbaFlow.Api
     dotnet user-secrets set "Entra:ClientSecret" "PASTE-THE-VALUE-HERE"
     cd ..\..
     ```
4. Start VerbaFlow again with `run.cmd`. The window should say `Outlook drafts: Email… can put the file in the person's own Outlook drafts`.

## How it works (for whoever looks after VerbaFlow)
- **Acting as the person:** VerbaFlow uses Microsoft's "on behalf of" sign-in. The person's own sign-in token is exchanged for a Microsoft Graph token with the delegated permission `Mail.ReadWrite`, so VerbaFlow can only reach that person's own mailbox (`/me/messages`). It never uses an application-wide mail permission.
- **Attachments:** files up to about 3 MB go in with the draft. Larger files are uploaded in pieces through an upload session.
- **What is recorded:** the file is recorded like any export (purpose `outlook-draft`, with its fingerprint), so a recipient's copy can still be checked. History shows `item.outlook_draft`, or `outlook_draft.failed` with the kind and a VF- reference. VerbaFlow does not know who the email goes to.
- **On a computer without Microsoft sign-in** (test users only), a stand-in saves each draft as an `.eml` file in `data/drafts`.
- **When signing in is set up but there is no client secret,** the button is not shown.
