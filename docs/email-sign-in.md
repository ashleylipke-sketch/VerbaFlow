# Sign-in with an email address (personal accounts)

For people with neither a Google nor a Microsoft account they want to use. On the sign-in page they type their email address, click **Email me a code**, and type the 6-digit code VerbaFlow emails them. There is no password to remember or lose. The first time, they also type their name (shown on their recordings). They get a **personal VerbaFlow**, exactly as with Google (see `google-sign-in.md`).

## When it is offered

- Whenever personal accounts are on (`Entra:PersonalAccounts=true` or `Google:ClientId` set), unless `SignIn:EmailCodes=false`.
- **On your own computer (Development) the codes are not really sent** unless Azure email is set up: they are written as email files to `src\VerbaFlow.Api\data\outbox`. Double-click the newest `.eml` file there to read the code (it opens in Outlook or Mail).
- **Online it stays off until real email is set up** (`Email:ConnectionString` and `Email:Sender`, see `azure-email-setup.md`), because otherwise nobody could receive their code.
- The start-up log says `Email sign-in: on …` and where the codes go.

## How it works (for whoever looks after VerbaFlow)

- `POST /api/sign-in/email/start` {email} (public): the address is tidied (small letters) and checked; a 6-digit code is made (`EmailCodes`, `EmailSignIn.cs`) and emailed through `IEmailSender`. Only a SHA-256 of the code is kept, in memory (a restart forgets it). At most one code a minute and five an hour per address; 20 sign-in requests a minute overall. A sending failure goes to Support: errors (area `email-sign-in`) and the person gets a plain sentence with a VF- reference.
- `POST /api/sign-in/email/finish` {email, code, name}: a right code within 10 minutes signs in once; five wrong guesses end the code. The person is matched on `email:<address>` (`EntraUsers.ResolvePersonalAsync`, History `user.first_sign_in` with account `email`) and gets VerbaFlow's own `vf1.` sign-in (30 days, renewed each time VerbaFlow opens). An empty name keeps the name given before.
- The answer to "start" is the same for any valid address, so it does not reveal who has an account.
- Someone who signs in with Google and with an email code using the same address gets two separate personal VerbaFlows.
- The Widgets link to an email account the same way as to Google: through the website (`widget.md`, "Personal accounts").
