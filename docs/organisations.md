# Several organisations

One VerbaFlow can serve many organisations. Each one has its own:

- people and administrators;
- recordings (meetings, dictations, Speech), with their notes, comments, attachments and History;
- vocabulary and saved text;
- letterhead and minutes templates;
- retention rules and holds;
- departments and department admins;
- Widget rules (where the Widget is switched off);
- plan, minutes and invoice.

Nobody in one organisation sees anything of another: not its recordings, not its people in any list, not its settings.
Administrators are administrators of their own organisation only. Personal accounts (students and others, see
`entra-sign-in-setup.md` step 13) stay apart from every organisation, as before.

## The first organisation

The organisation VerbaFlow was set up for (Microsoft directory `Entra:TenantId`) is the **first organisation**. Everything
made before there were several organisations belongs to it and stays exactly as it was. Its name is set with:

```
dotnet user-secrets set "Organisation:Name" "Your organisation's name"
```

(left out, it is called "Your organisation"). Its plan is set as before (see `billing.md`).

## Adding an organisation (VerbaFlow support)

1. Sign in as VerbaFlow support and open **More ▾ → Organisations**.
2. Under **Add an organisation**, type its name and its **Microsoft directory** (the customer's Tenant ID: Microsoft Entra
   admin centre → Overview → Tenant ID). Click **Add organisation**. It starts on the **Trial** plan.
3. Click its plan (in the Plan column) to set its plan and Stripe customer reference.

**Change** renames an organisation or changes its Microsoft directory.

## Letting the customer's people sign in (once, for VerbaFlow)

VerbaFlow's app registration must accept sign-ins from other directories:

1. In the Entra admin centre, open **App registrations › VerbaFlow › Manifest**, set `"requestedAccessTokenVersion"` to `2`
   and click **Save** (already done if personal accounts are on).
2. Open **Authentication** and under **Supported account types** choose **Accounts in any organizational directory**
   (or, if personal accounts are on, **Accounts in any organizational directory and personal Microsoft accounts**). Click **Save**.
3. In `src\VerbaFlow.Api` run:

   ```
   dotnet user-secrets set "Entra:OtherOrganisations" "true"
   ```

   and restart `run.cmd`.

## What the customer's IT administrator does (once per organisation)

1. **Allow VerbaFlow in their directory.** Their Global Administrator opens this address (with their own Tenant ID and
   VerbaFlow's Application (client) ID filled in) and clicks **Accept**:

   ```
   https://login.microsoftonline.com/<their tenant id>/adminconsent?client_id=<VerbaFlow client id>
   ```

2. **Choose their VerbaFlow administrators.** In their Entra admin centre: **Enterprise applications › VerbaFlow › Users and
   groups › Add user/group**, pick the people, choose the role **Admin**, and click **Assign**.
3. Optional, for departments: their groups come through the same way as for the first organisation
   (`entra-sign-in-setup.md` step 11 is done once on VerbaFlow's app registration and covers every organisation).

After that, anyone in their directory who signs in with Microsoft lands in their organisation. A work account from a
directory VerbaFlow does not know becomes a personal account when personal accounts are on, and is refused otherwise.

Someone who used VerbaFlow with a personal account before their directory was added gets a new, empty organisation account:
their personal recordings stay in their personal account.

## Trying it on your own computer

The test users include a second, made-up organisation, **Northwind (test)**: **Olivia Admin (Northwind)** (its
administrator) and **Oscar (Northwind)**. Record something as Oscar and as Alice: Olivia sees only Oscar's, Carol only
Alice's. **Sam Support** sees the Organisations page.

## Not separated yet

- The services VerbaFlow itself pays for are shared: Azure Speech, Azure OpenAI, email sending, Recall.ai.
  Each organisation's use is counted against its own plan.
- **Microsoft 365 calendars** and **Put it in my Outlook drafts** use VerbaFlow's link with the first organisation's
  directory, so they are offered to the first organisation's people only. Other organisations can book from a calendar
  link (iCal) and use Open a new email.
- The Microsoft groups list on the Departments page shows only the groups of the admin's own organisation's people.

## How it works (for whoever looks after VerbaFlow)

- `Core/Domain/Organisation.cs`: `Organisation` (table `doc_organisations`) and `Organisations.Default`, the first
  organisation's fixed id, which is stored as **null** everywhere (`User.OrganisationId`, `Item.OrganisationId`,
  `Department/MinutesTemplate/NoteTaker.OrganisationId`), so older rows need no change.
- `User.Org` / `Item.Org` (Default for the first, null for a personal account), `User.SameOrganisation(...)`.
  `PermissionEvaluator`: administrators, grants, departments and department admins only reach their own organisation.
- One-of-a-kind settings rows (letterhead, retention rules, Widget rules) use `Organisations.Key(org, oldKey)`: the first
  organisation keeps its old key, others use their own id. Vocabulary and saved text use `User.Space`.
- Sign-in (`SignIn.cs`): with `Entra:OtherOrganisations` or `Entra:PersonalAccounts`, tokens from any directory pass the
  issuer check; `OrganisationService.ByTenantAsync(tid)` then decides: a known directory makes the person a member of that
  organisation (`SignIn.Member`, `EntraUsers` matches on object id and organisation), anything else is personal.
- History: changes to an organisation's settings carry `organisation` (left out for the first organisation);
  `organisation.created/changed`. Notifications to administrators go to the organisation the event is about.
- Billing: each organisation's account is its id (the first keeps the old one); usage goes to Stripe per account.
- API (support only): `GET/POST /api/organisations`, `PUT /api/organisations/{id}`; `GET /api/plan?organisation=`,
  `POST /api/plan/organisation` {plan, customerId, organisation}. `/api/me` adds `organisation` and `severalOrganisations`.
