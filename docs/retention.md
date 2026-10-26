# Retention: how long recordings are kept

Administrators decide how long VerbaFlow keeps recordings. There are separate rules for **meetings** (recorded, imported
and online meetings) and for **dictations** (Dictate and Speech). Each rule is a number of days, or empty to keep for ever.
Until an organisation sets rules, everything is kept.

| Rule | What happens |
|---|---|
| Delete approved ones, N days after approval | Every version of the recording is deleted: sound or video, text, summaries. The line in the list ("Deleted") and its History stay, as with any delete. |
| Remove only the sound or video of approved ones, N days after approval | The recording's audio or video files are removed. The text, minutes and History stay; the item stays approved. It can no longer be played, converted again or changed with Insert or Amend. Must be sooner than the delete rule. |
| Delete ones never approved, N days after anything last happened to them | "Anything" is any entry in the item's History (an edit, playing it, assigning it...). |

## Changing the rules (two administrators)

1. An administrator opens **Retention**, types the days and clicks **Propose this change** (optionally saying why).
2. A *different* administrator opens **Retention** and clicks **Agree** (or **Reject**). The proposer can **Withdraw** it.
3. Nothing is deleted under the new rules until **a week after** they are agreed, so a mistake can be put right first.

Because two administrators agree to the rules, the rules may delete approved recordings without the usual two-administrator
delete request for each one.

## Holds

An administrator can open a recording and click **Put on hold** (a reason is required, for example a complaint or a legal
case). While on hold, the rules never touch any version of it. **Release the hold** lets the rules apply again.

The Retention page shows administrators what is **due in the next 30 days** and everything **on hold**. Each item page
says what the rules will do to it and when.

## How it runs

`RetentionWorker` applies the rules every hour (`Retention:Worker=false` stops it). Items being converted are skipped
until next time. History: `retention.proposed`, `retention.agreed`, `retention.withdrawn`, `retention.rejected`,
`retention.held`, `retention.released`, `item.deleted` (by "the retention rules"), `retention.recording_removed`.
Problems go to Support: errors (area `retention`) with a VF- reference.

Code: `Core/Domain/Retention.cs`, `Infrastructure/Services/RetentionService.cs`, `Api/RetentionEndpoints.cs`,
`web/src/pages/Retention.tsx`, `web/src/RetentionLine.tsx`. Tables `doc_retention`, `doc_retention_holds`,
`doc_recording_removals`.

Not built: legal-hold export, per-department rules, keeping exported files' records for a set time (export records are
append-only and always kept).
