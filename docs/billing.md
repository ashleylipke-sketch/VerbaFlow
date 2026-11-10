# Plans and billing

VerbaFlow counts the minutes of speech it turns into text and limits them by plan.

## What counts
- Each recording or imported file converted to text: its length. A re-run counts again.
- Speech mode: the sound heard while speaking (the live text is then used, so the recording is not counted twice).
- The Widget (browser, Windows, phones): the sound heard while it listens.

The count starts again on the 1st of each month (UTC). Page: **More ▾ → Your plan** (personal accounts) or **Settings ▾ → Plan** (administrators, VerbaFlow support).

## Personal accounts: paid by card
| Plan | Minutes a month | Price (placeholder) |
|---|---|---|
| Free | 30 minutes | £0 |
| Basic | 5 hours | £6.99 a month |
| Student | 5 hours | £3.99 a month (needs a student email address, checked once a year) |
| Professional | 20 hours | £14.99 a month |

When the minutes are used up, new conversions wait (they show as "Conversion failed" with the reason, and **Retry** works once there are minutes again) and new Speech mode or Widget sessions are refused with a plain sentence. Speech already under way is never cut off. The person is told at 80% and 100% (bell and email, group "Plan").

### Switching plans
Someone on a paid personal plan switches on **Your plan** with **Switch to …**, on the same card:
- **A plan with more minutes** starts at once. The difference in price for the rest of the month is charged straight away (if the card is refused, nothing changes and they are told to check the card under Manage the plan).
- **A plan with the same or fewer minutes** starts when the month they paid for ends. They keep the plan they paid for and its minutes until then; nothing is refunded. Until then the page shows "You are switching to … on …" with **Keep …** to call it off.
- Going back to **Free** is done by cancelling under **Manage the plan**.
- A cancelled plan cannot be switched: keep it first under Manage the plan.

History: `plan.switch_requested` (from, to, starts: now / at renewal / kept), then `plan.changed` when the payment service confirms.

### Student check
The Student plan needs a school, college or university email address. Choosing Student opens **Student check**: the person types that address, VerbaFlow emails it a 6-digit code (10 minutes, same limits as email sign-in), they type it back, and the plan carries on (to the pretend payment page, or the switch).
- Counted as a student address: ending in `.edu`, or `.ac.` / `.edu.` before a country ending (`.ac.uk`, `.ac.za`, `.ac.nz`, `.edu.au`…).
- Many universities (for example in Europe) use plain addresses such as `uni-heidelberg.de`. Add them in settings, separated by commas (each also covers its sub-addresses such as `stud.uni-heidelberg.de`):
  ```
  dotnet user-secrets set "Billing:StudentDomains" "uni-heidelberg.de,uva.nl"
  ```
- The check lasts a year. From 30 days before it runs out, the page asks to check again. Once it has run out, the plan moves to **Basic** when the month paid for ends (checked every 5 minutes; History `plan.student_check_expired`); checking again before then keeps Student.
- One student address serves one account. Only the part after the @ and a fingerprint (SHA-256) of the address are kept, never the address; History `plan.student_checked` (domain, until).

## Organisations: invoiced monthly
| Plan | Minutes a month (shared) | Price (placeholder) | People | Extra hours |
|---|---|---|---|---|
| Trial | 2 hours | £0 | up to 5 | not allowed: stops at the limit |
| Basic | 100 hours | £49 a month | up to 25 | £1.20 each |
| Business | 400 hours | £179 a month | up to 100 | £1.10 each |
| Professional | 1,000 hours | £399 a month | up to 250 | £1.00 each |
| Enterprise | 3,000 hours | agreed with VerbaFlow | | £0.90 each |

Everyone in the organisation shares the minutes. "People" is shown, not enforced. On a paid plan nothing is ever refused: minutes over the plan go on the next month's invoice. Administrators are told at 80% and 100%. Administrators cannot buy or change the plan: VerbaFlow support sets it on the Plan page (plan and the Stripe customer reference), or it is fixed in settings.

## Changing prices and minutes (no code change)
Settings, per plan id (`free`, `personal-basic`, `personal-student`, `personal-professional`, `org-trial`, `basic`, `business`, `professional`, `enterprise`):

```
dotnet user-secrets set "Billing:Plans:basic:Price" "£59 a month"
dotnet user-secrets set "Billing:Plans:basic:MinutesPerMonth" "9000"
```
Also `Name`, `People`, `ExtraPerHour`, `StripePriceId`. Other settings:
- `Billing:Enabled=false`: no limits and no counting.
- `Billing:OrganisationPlan` (e.g. `basic`) and `Billing:OrganisationCustomerId` (e.g. `cus_ABC123`): fix the organisation's plan here instead of on the Plan page.

**Testing on your own computer:** the organisation starts on the Trial (2 hours a month). To keep testing without a limit, set `Billing:OrganisationPlan` to `basic`.

## Until Stripe is set up
Choosing a personal plan opens a **pretend payment page**: nothing is charged and no card is asked for. "Manage the plan" opens a pretend page to cancel. The organisation's extra minutes are counted but not sent anywhere.

## Setting up Stripe (when you are ready to charge)
Never paste a key into a chat. Use `dotnet user-secrets` (or the online app's secrets).
1. Make a Stripe account at stripe.com (use Test mode first).
2. **Personal plans:** Products → Add product for Basic, Student and Professional, each with a monthly recurring price. Copy each price id (`price_…`) and set `Billing:Plans:personal-basic:StripePriceId` etc.
3. **Organisation extra minutes:** Billing → Meters → Create meter, event name `verbaflow_speech_seconds`, aggregation Sum. Then for each organisation plan make a product with a monthly fixed price (e.g. £49) plus a usage price on that meter, graduated: the first tier (the included minutes, in seconds: 100 hours = 360,000) at £0, then the extra-hour price divided by 3,600 per second. Make the organisation a customer, subscribe it, and put its `cus_…` reference on the Plan page as VerbaFlow support. Set `Stripe:MeterEventName` to the meter's event name.
4. Developers → API keys: set `Stripe:SecretKey`.
5. Developers → Webhooks → Add endpoint `https://<VerbaFlow address>/api/payments/stripe`, events `checkout.session.completed`, `customer.subscription.created`, `customer.subscription.updated`, `customer.subscription.deleted`. Set `Stripe:WebhookSecret` to its signing secret (`whsec_…`).
6. Settings → Billing → Customer portal: switch on cancelling and updating the card. Leave switching plans off there: VerbaFlow's own Switch buttons apply the rules above (bigger at once, smaller at renewal, Student only after the student check).
7. Restart VerbaFlow. The start-up line `Billing:` says what is in use.

VerbaFlow sends the organisation's seconds to the meter every 5 minutes; each sending has its own identifier, so a repeat is counted once.

## Not built yet
- A way for VerbaFlow support to mark someone a student whose address is not recognised (add their university in `Billing:StudentDomains` instead).
- Real Stripe has not been tried, including switching plans.
