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
| Student | 5 hours | £3.99 a month (nobody checks that the person is a student yet) |
| Professional | 20 hours | £14.99 a month |

When the minutes are used up, new conversions wait (they show as "Conversion failed" with the reason, and **Retry** works once there are minutes again) and new Speech mode or Widget sessions are refused with a plain sentence. Speech already under way is never cut off. The person is told at 80% and 100% (bell and email, group "Plan").

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
6. Settings → Billing → Customer portal: switch on cancelling, updating the card and switching between the personal plans.
7. Restart VerbaFlow. The start-up line `Billing:` says what is in use.

VerbaFlow sends the organisation's seconds to the meter every 5 minutes; each sending has its own identifier, so a repeat is counted once.

## Not built yet
- More than one organisation on one VerbaFlow (each installation is one organisation).
- Checking that a Student is a student.
- Switching between paid personal plans inside VerbaFlow (use Manage the plan, Stripe's page).
- Real Stripe has not been tried.
