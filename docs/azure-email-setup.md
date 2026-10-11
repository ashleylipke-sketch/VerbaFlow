# Azure Communication Services Email: sending exported minutes

With this connected, **Email it** on a meeting's Export box sends the Word or PDF minutes as an attachment. Without it nothing leaves the computer: each email is saved as an `.eml` file in `src\VerbaFlow.Api\data\outbox`, which Outlook or Windows Mail can open, so the whole flow can be tried first.

**Untested against real Azure.** The stand-in outbox is tested; real sending has not been tried yet.

## 1. Create the two resources

1. In the Azure portal, create an **Email Communication Services** resource. Choose the data location your data-protection rules require.
2. Open it, go to **Provision domains**, and add an **Azure managed domain** (quickest, gives an address like `DoNotReply@xxxx.azurecomm.net`). A custom domain such as your company's can be added later; it needs DNS records.
3. Create a **Communication Services** resource in the same data location.
4. Open the Communication Services resource, go to **Email > Domains**, and **Connect domain**: pick the email resource and the domain from step 2.
5. On the Communication Services resource, open **Keys** and copy the **connection string** (it starts with `endpoint=https://`).
6. Note the sender address from the domain's **MailFrom addresses** (for example `DoNotReply@xxxx.azurecomm.net`).

## 2. Give the app the details

In a Command Prompt, in the `src\VerbaFlow.Api` folder (add `set PATH=C:\Program Files\dotnet;%PATH%` first if `dotnet` is not found):

```
dotnet user-secrets set "Email:ConnectionString" "paste-the-connection-string-here"
dotnet user-secrets set "Email:Sender" "DoNotReply@xxxx.azurecomm.net"
```

The connection string contains a key and is stored outside the repository. Never paste it into a chat, a file in the repository, or a commit.

## 3. Check it

Run `run.cmd`. The startup log should say `Email: sending through Azure Communication Services as ...`. Open a meeting, use **Email it** with your own address, and check your inbox (and junk folder: Azure-managed domains are often filtered at first).

## How it behaves

- Who may send: the same people who may export (the owner, admins, and anyone granted Export).
- Up to 10 addresses per email. Every address is checked first; nothing is sent if one is not valid.
- Subject `Meeting minutes: <name>`, with `(DRAFT)` or `(SUPERSEDED)` added when the meeting is not approved. The optional message is added as plain text. Replies go to the sender's own address.
- Every email is an export: the file's fingerprint is recorded, so a recipient's copy can be checked later with **Check an exported file**. The history shows `item.emailed` with the addresses, the format and the fingerprint.
- If sending fails, the person sees a plain sentence with a reference code (VF-XXXXXX); the technical detail goes to the support-only error page. The history shows `email.failed`.

## Notes

- Limits to verify on your subscription: attachment size (Microsoft documents about 10 MB per email in total) and how many emails per minute and hour a new resource may send. Long transcripts make bigger files.
- Cost: billed per email and per megabyte. Check current pricing.
- Production should use keyless Microsoft Entra authentication instead of a connection string.
