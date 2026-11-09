# Azure Communication Services Email: sending real email

VerbaFlow sends three kinds of email: **sign-in codes** (email sign-in), **notifications** (the bell, for people who ticked By email) and **minutes or letters** (Export, then Email, then Send from VerbaFlow). Until this is set up nothing leaves the computer: each email is saved as an `.eml` file in `src\VerbaFlow.Api\data\outbox`, which Outlook or Windows Mail can open.

**Not yet tried against real Azure.** The outbox is tested; real sending has not been seen working yet.

It takes about 15 minutes. You need the Azure subscription you already use for Speech and OpenAI.

## 1. Make the email resource and its address

1. Go to https://portal.azure.com and sign in.
2. In the search box at the top, type **Email Communication Services** and open it. Click **+ Create**.
3. Fill in:
   - **Subscription**: the one you use for VerbaFlow.
   - **Resource group**: pick the one your Speech resource is in (or **Create new** and call it `verbaflow-email`).
   - **Name**: for example `verbaflow-email`.
   - **Data location**: the place your data-protection rules require (for example Europe, UK or Africa, whichever is listed and fits).
4. Click **Review + create**, then **Create**. Wait until it says the deployment is complete, then click **Go to resource**.
5. On the left, click **Provision domains** (sometimes under **Settings**), then **Add domain**, then **Azure domain** (also called *1-click add* or *Azure managed domain*). Wait a minute or two until it shows as **Verified** / ready.
6. Click the new domain (it looks like `xxxxxxxx-xxxx-....azurecomm.net`). Under **MailFrom addresses** you see the sender address, usually `DoNotReply@xxxxxxxx-....azurecomm.net`. Keep this page open: you need that address in step 3.

## 2. Make the Communication Services resource and connect the address

1. In the search box at the top, type **Communication Services** (without "Email") and open it. Click **+ Create**.
2. Same subscription and resource group, a name such as `verbaflow-comms`, and **the same data location** as in step 1. **Review + create**, then **Create**, then **Go to resource**.
3. On the left, click **Email**, then **Domains**, then **Connect domain**. Pick the email resource from step 1 and its domain, and click **Connect**.
4. On the left, click **Keys** (under **Settings**). Next to **Connection string** (Primary key), click the copy button. It starts with `endpoint=https://`. **It contains a key: do not paste it into a chat, an email or any file in the VerbaFlow folder.**

## 3. Give VerbaFlow the details

Open a Command Prompt and type (the first line only if `dotnet` is not found):

```
set PATH=C:\Program Files\dotnet;%PATH%
cd <your VerbaFlow folder>\src\VerbaFlow.Api
dotnet user-secrets set "Email:ConnectionString" "<right-click to paste the connection string here>"
dotnet user-secrets set "Email:Sender" "DoNotReply@xxxxxxxx-....azurecomm.net"
```

Keep the quotes. Use the sender address from step 1.6. These are stored on your computer outside the VerbaFlow folder, so they never reach GitHub.

## 4. Check it

1. Close VerbaFlow if it is running and start it again with `run.cmd`.
2. In the black window, look for the line starting `Email:`. It should say `sending through Azure Communication Services as DoNotReply@...`. If it still says `STAND-IN`, one of the two settings is missing or misspelt.
3. Open VerbaFlow, sign in as an administrator, and go to **Settings ▾ → Email**. It should name the sender address. Type your own address (any address you can read, Gmail is fine) and click **Send a test email**. It waits up to a minute for Azure's answer and tells you whether Azure passed the email on.
4. Look in that inbox, **and the junk or spam folder**: emails from an `azurecomm.net` address are often filtered at first. Mark it as not junk.
5. If it says it could not be sent, it gives a reference (VF-XXXXXX). The technical detail is on the **Support: errors** page under that reference.

Then try the real things: sign out and use **Sign in with your email address**, or Export a meeting and use **Email… → Send it from VerbaFlow instead**.

## Limits of the free Azure address

The `azurecomm.net` address is meant for trying things out. Microsoft gives it low sending limits (a handful of emails a minute and a few dozen an hour; check the current numbers on Microsoft's *Service limits for Azure Communication Services* page), and they cannot be raised. Before other people rely on VerbaFlow (sign-in codes in particular), add your **own domain** (for example `verbaflow.co.za`) under **Provision domains → Add domain → Custom domain**: Azure shows DNS records to add where the domain is registered, and once it is verified you connect it as in step 2.3 and change `Email:Sender` to an address on it, such as `DoNotReply@verbaflow.co.za`. Higher limits on your own domain can be asked for from Microsoft support.

Cost: billed per email and per megabyte sent; a single email costs a small fraction of a cent. Check current pricing on Microsoft's page.

## How it behaves

- Sign-in codes: see `docs/email-sign-in.md`. Notifications: see `docs/notifications.md`.
- Minutes and letters: who may send is the same as who may export (the owner, admins, and anyone granted Export). Up to 10 addresses per email. Every address is checked first; nothing is sent if one is not valid.
- Subject `Meeting minutes: <name>`, with `(DRAFT)` or `(SUPERSEDED)` added when the meeting is not approved. The optional message is added as plain text. Replies go to the sender's own address.
- Every email is an export: the file's fingerprint is recorded, so a recipient's copy can be checked later with **Check an exported file**. The history shows `item.emailed` with the addresses, the format and the fingerprint.
- If sending fails, the person sees a plain sentence with a reference code (VF-XXXXXX); the technical detail goes to the support-only error page. The history shows `email.failed`.

## Notes

- Limits to verify on your subscription: attachment size (Microsoft documents about 10 MB per email in total) and how many emails per minute and hour a new resource may send. Long transcripts make bigger files.
- The test email: administrators only, at most 10 an hour each; History shows `email.test_sent` (address, service, result) or `email.test_failed` (reference).
- Production should use keyless Microsoft Entra authentication instead of a connection string.
