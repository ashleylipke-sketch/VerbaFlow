# Errors: what customers see and what support sees

**Rule: customers never see technical error text.** That includes their administrators. They see a plain sentence and a short reference code, for example:

> The transcription service is busy right now. Wait a few minutes and try again. Reference: VF-7K2Q9M.

The reference code is how support finds the detail.

## What is recorded where

| Where | What it contains | Who can see it |
|---|---|---|
| The message on the item or in the page | A plain sentence and `Reference: VF-XXXXXX` | The customer |
| The item's history (audit trail) | The kind of problem and the reference code only | Anyone who can read the item |
| The support error table (`doc_support_errors`) | Full technical detail: exception, provider response, item id, time | VerbaFlow support only |
| The server log | The same detail, with the reference | Whoever runs the server |

Messages written for customers are in `VerbaFlow.Core/Domain/Failures.cs` (`CustomerMessages`). Failures from Azure carry a kind (busy, unreachable, settings, audio, blocked, other) which chooses the sentence. Technical text, including advice such as changing the Azure pricing tier, stays in the detail.

## Looking up a reference (support and developers)

Sign in as a support user. A **Support: errors** link appears in the top bar. Type the reference code and press Find, or press Show latest. Click a row's detail to see the full technical text. Customers and customer administrators never see the link, and the server refuses them if they open the address directly (`/api/support/errors`).

Support users deliberately **cannot see customers' meetings, recordings or transcripts**. They see an empty meeting list. They work from the reference code and the technical detail only. (Looking inside a customer's item would need a separate, audited access process, which is not built.)

In development the support user is "Sam Support" (`X-Dev-User: sam`). In production, support access will come from a VerbaFlow-owned group in Microsoft Entra ID, separate from any customer's administrators. That mapping is not built yet.

## Rules for new code

- Never put `ex.Message`, a provider's response, a file path or a setting name in anything a customer can read.
- Catch failures from outside services and pass them to `FailureReporter.ReportAsync(area, itemId, ex)`. Show the customer `UserMessage`, and store only `Kind` and `Reference` in the history.
- Messages that tell the user something they can fix themselves are fine, for example "The text cannot be empty" or "Allow the microphone in your browser".
- Throw `ProviderException` with the right kind from any new adapter.
