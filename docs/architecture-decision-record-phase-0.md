# Architecture decision record: phase 0

**Products:** Speak, Meet, Widget (working titles) | **Cloud:** Microsoft Azure | **Status of every decision below:** Proposed

## Purpose

Phase 0 confirms the technology choices before building starts. Each decision lists the proposal, the alternatives, and the test that would confirm or overturn it. A decision becomes Accepted only when its test is passed and the owner signs off.

## Summary

| ID | Decision | Proposal | Main risk |
|---|---|---|---|
| 01 | Region and residency | UK South, UK West for recovery | Model availability by region |
| 02 | Identity | Microsoft Entra ID with group mapping | Group design in your tenant |
| 03 | Backend | C# on .NET in Azure Container Apps | Team skills |
| 04 | Database | Azure SQL or PostgreSQL Flexible Server | Transcript version model |
| 05 | Storage | Blob Storage, immutable, customer-managed keys | Video storage cost |
| 06 | Audit trail | Hash-chained table plus immutable export | Proving it is tamper-evident |
| 07 | Batch speech | Azure AI Speech vs MAI-Transcribe-2 | Diarization on one-microphone rooms |
| 08 | Streaming speech | Azure AI Speech real-time | Latency and language switching |
| 09 | Noise filtering | On-device plus server-side enhancement | Over-filtering harms accuracy |
| 10 | Generative AI | Azure OpenAI in Foundry, plus Azure AI Translator | Data handling terms |
| 11 | Meet recording bots | Teams first; build vs buy for other platforms | Platform admission rules |
| 12 | Widget clients | Browser extension first | OS limits on other apps |
| 13 | Signing | Key Vault HSM plus PDF library plus timestamp | Certificate choice |
| 14 | Virus scanning | Defender for Storage malware scanning | Unscannable files |
| 15 | Telephony (call recording in Meeting mode) | Speak calling via Azure telephony as the baseline, regional carrier adapters later | Number availability, consent law and data residency by country |

## Evaluation set (used by decisions 07, 08, 09)

Build one shared test set before any speech testing, so providers are compared fairly.

- Real recordings from your own users, with permission, plus synthetic ones where consent is not available.
- Cover: one laptop in the middle of a table, a conference microphone, a noisy room, overlapping speech, a dictated letter, and calls that switch language mid-sentence.
- Include your own terminology: client names, legal clauses, numbers such as "Legal 1.1", and the languages you actually need.
- Human-corrected reference transcripts for every file.
- Metrics: word error rate, speaker error rate, language-switch accuracy, snippet command recognition rate, and cost per hour of audio.
- Agree pass targets before testing, not after seeing results.

## Decisions

### 01. Region and data residency
- **Proposal:** Deploy in UK South, with UK West for disaster recovery, as the home region. Because the product is used internationally, recordings are stored in a region chosen per country (see decision 15 and the jurisdiction table), with the UK as the default.
- **Alternatives:** An EU region only; a single global region; multi-region active/active.
- **Confirming test:** Check every planned service (speech, generative AI, malware scanning, Key Vault HSM) is available in each region you intend to use and meets your retention terms. Legal confirms residency requirements for client recordings in each launch country.
- **Overturned if:** A required AI model is unavailable in-region and Legal will not accept another region.

### 02. Identity and permissions
- **Proposal:** Microsoft Entra ID for SSO. Departments and default roles come from groups. Per-capability rights (read, write, re-assign, export) and the admin hierarchy are stored in the application.
- **Alternatives:** A separate identity provider; storing all roles in Entra only.
- **Confirming test:** Prove that a user's department and roles update within an agreed time after a group change, and that department renames do not break snippet prefixes (which are linked to a stable department ID).

### 03. Backend platform
- **Proposal:** C# on current .NET, hosted in Azure Container Apps. Windows virtual machines only for Meet's recording bots.
- **Alternatives:** Node.js or Python backend; AKS from the start.
- **Confirming test:** Build a thin vertical slice (sign in, upload, queue a job, store a result) and confirm the team can deploy and monitor it. Revisit AKS only if scaling or networking needs exceed Container Apps.

### 04. Database
- **Proposal:** Azure SQL or PostgreSQL Flexible Server for users, items, statuses, permissions and transcript versions. Azure AI Search for search.
- **Alternatives:** Cosmos DB.
- **Confirming test:** Model the transcript as version 1 plus edits, with language and speaker tags per passage and timestamps into the original audio. Confirm that comparing, restoring and searching versions is fast on a realistic volume.

### 05. Storage and retention
- **Proposal:** Blob Storage with versioning, immutability policies for originals, customer-managed keys in Key Vault, lifecycle rules, and cooler tiers for video.
- **Alternatives:** Azure Files; storing audio in the database.
- **Confirming test:** Confirm that an original cannot be altered or deleted before its retention date, that hashes recorded at capture still match after retrieval, and that purge and tombstone rules work through the platform retention service.

### 06. Audit trail
- **Proposal:** An append-only table where each entry carries the hash of the previous one, exported regularly to immutable storage and Log Analytics. Widget writes content-free entries only.
- **Alternatives:** A dedicated ledger database; relying on Log Analytics alone.
- **Confirming test:** An independent check that detects a deliberately altered or deleted entry. Confirm that Widget entries never contain text or audio.

### 07. Batch transcription, diarization and language identification
- **Proposal:** Azure AI Speech batch transcription with candidate languages and diarization enabled. Benchmark it against MAI-Transcribe-2, which covers 60 languages and includes diarization and word-level timestamps. It is a very recent release, so treat it as a candidate and not a default.
- **Alternatives:** Other providers hosted on Azure.
- **Confirming test:** Run the evaluation set through each. Compare word error rate, speaker error rate, correct language per segment, handling of overlapping speech, and cost per hour. Check that word timestamps map cleanly to the original audio.
- **Overturned if:** Neither meets the agreed targets on one-microphone meeting audio. The fallback is multi-channel capture guidance and more reviewer tooling.

### 08. Streaming transcription (Speech mode and Widget)
- **Proposal:** Azure AI Speech real-time recognition with language identification.
- **Alternatives:** A streaming model from another provider.
- **Confirming test:** Measure the delay from speaking to text appearing, accuracy when switching language, and behaviour on a poor connection. Confirm that the raw microphone signal can be saved alongside the stream, and that snippet commands are recognised reliably once normalised.

### 09. Noise filtering
- **Proposal:** On-device suppression for the live stream, plus a server-side enhancement step before batch transcription. Filtering applies to the processing copy only. The original is never altered.
- **Alternatives:** Provider-side noise robustness alone.
- **Confirming test:** Compare transcription accuracy with no filter, moderate and strong. Choose the setting that helps without clipping quiet words. Confirm the filtered copy is deleted after approval and the setting and model version are logged.

### 10. Generative AI and translation
- **Proposal:** Azure OpenAI in Microsoft Foundry for summaries, minutes, action points, tone analysis and formatting. Azure AI Translator for translation.
- **Alternatives:** Using a generative model for translation too.
- **Confirming test:** Reviewers score outputs on the evaluation set for accuracy and for leaving inserted snippets untouched. Tone analysis is checked to use speech only, never attachments. Obtain written confirmation of no training on your content and the retention and abuse-monitoring settings for your tenant.
- **Overturned if:** The data handling terms are not acceptable to Legal.

### 11. Meet recording bots
- **Proposal:** Build the Teams bot first. An application-hosted media bot needs Windows Server in Azure and Microsoft's .NET media library. For Zoom, Webex and Google Meet, decide between building each integration and buying a multi-platform service.
- **Alternatives:** Capturing audio from users' devices instead of joining as a bot.
- **Confirming test:** Join and record test meetings that include external guests, lobbies and locked-down tenants. A community answer suggests recording does not require a compliance policy on the user, but it is not official guidance, so get Microsoft to confirm. For the buy option, Legal and security must approve recordings passing through a third party.
- **Also needed:** The bot's joining announcement and consent wording, reviewed by Legal.

### 12. Widget clients
- **Proposal:** Browser extension first. Then Windows, Mac, Android, and last iOS as a dictation keyboard. Distribute through Intune with admin block lists.
- **Alternatives:** A single cross-platform desktop app.
- **Confirming test:** In the extension, confirm the red, amber and green states, the locked field after Accept, and the double-click amend flow on a representative set of sites and web email. Confirm Widget never activates in password or payment fields, and holds nothing after Accept.

### 13. Digital signing
- **Proposal:** The organisation certificate held in Key Vault Managed HSM, a PDF signing library that calls the HSM for the signature, and a trusted timestamp on every signature.
- **Alternatives:** Per-approver certificates (rejected for now).
- **Confirming test:** Signed PDFs show as valid in common PDF readers, show as modified when a character is changed, and remain verifiable after the certificate expires. Rehearse certificate renewal and revocation.

### 14. Virus scanning of attachments and imports
- **Proposal:** Microsoft Defender for Storage malware scanning, with files quarantined until the scan passes.
- **Alternatives:** A scanning service inside the processing pipeline.
- **Confirming test:** Test with standard test files, encrypted archives and oversized files. Confirm that unscannable files are blocked and flagged to an admin, and that stored files are re-scanned periodically.

### 15. Telephony: recording telephone calls in Meeting mode

**Requirements settled with the product owner**
- Inbound and outbound calls.
- Recording is switched on by the user for each call. It is never automatic and the choice is not remembered.
- If recording cannot start, the user is told and chooses whether to continue the call unrecorded.
- Users should appear to call from their own number where possible. Where that is not possible, a Speak-provisioned local number is acceptable.
- The first release covers the UK, Ireland, Spain, France, the USA, Canada, South Africa and Australia.
- Users do not currently have business numbers from a global calling provider, so numbers will be provisioned.
- No dependency on Microsoft Teams for the baseline.

**Why not record on the phone itself**
Phone operating systems restrict apps from capturing call audio. iOS does not let apps access it, and on Android third-party recording was removed in 2022, leaving recording to manufacturer dialers that vary by device, version and region. Native recording is therefore treated as an optional import route, never the foundation.

**Proposal: one telephony interface, several adapters**

| Tier | How the call is recorded | Number shown | Where it applies |
|---|---|---|---|
| 1. Speak calling (baseline) | Calls are placed and received in the Speak app through Azure telephony and recorded on the server | A local number provisioned for the user in their country | Every launch country where numbers can be obtained |
| 2. Regional carrier adapters | A carrier or Teams-based service records and Speak ingests the result | The user's own mobile number | Only in markets where an adapter is signed up, and only if it can start recording on demand, per call |
| 3. Native recording import | The phone records and the user imports the file, through the existing Imported status | Their own | Only where the phone offers it |
| 4. Speakerphone capture | Meeting mode records the room | Their own | Anywhere, at lower quality |

- Tier 1 is built first. Tiers 2 to 4 are added per market as business need justifies, and all feed the same pipeline: hash on arrival, noise handling, transcription, diarization, language detection and AI outputs.
- Tier 2 services that record every call on a SIM automatically conflict with the per-call rule and are excluded unless they can be triggered per call.
- Bridged calls are recordings made by the system and appear in **With Author**. Recordings supplied as files appear in **Imported**.

**Per-call recording rules**
1. The user presses Record before dialling or during the call. The default is off for every call.
2. Recording starts only after the consent step for the applicable country rule has completed. The other party hears an announcement, or is asked to press a key to agree, depending on the rule.
3. If recording starts mid-call, the item states how far into the call it began.
4. A persistent "Recording" indicator is shown to the user while recording.
5. Pausing needs a logged reason and the gap is marked on the timeline.
6. Calls that are not recorded create no item, transcript or call detail. Only a content-free audit entry exists if recording was attempted.

**If recording cannot start or fails**
- The user is told immediately, in plain words, and chooses **Continue without recording**, **Retry** or **End call**.
- Continuing never skips the consent step. If the announcement cannot play, or explicit consent is not given, nothing is recorded whatever the user chooses.
- If recording fails mid-call, the partial recording is kept and marked incomplete. A restart creates a linked second part with a new announcement and a marked gap.
- Each failure is logged without content: the attempt, a reason code, the user's choice and the time.

**Jurisdiction table (starting positions for Legal to confirm, not legal advice)**

| Country | Consent mode | Continue after objection | Storage region |
|---|---|---|---|
| United Kingdom | Announce, implied | Yes (product owner's rule) | UK |
| Ireland | Announce, implied | Yes | EU |
| Spain | Announce, implied | Yes | EU |
| France | Explicit (key press) | To be set by Legal | EU |
| United States | Explicit (key press) | To be set by Legal | US |
| Canada | Announce, implied (confirm provincial rules) | Yes | Canada |
| South Africa | Announce, implied | Yes | South Africa |
| Australia | Explicit (key press) | To be set by Legal | Australia |

- For each call, the stricter rule of the user's country and the other party's country applies.
- US and Australian rules vary by state, and mobile numbers do not reliably show where someone is, so one conservative rule is used.
- Storage follows the user's country by default. Speech and generative AI services must be available in each region.
- "Continue after objection" is a per-country setting. Where Legal does not accept continuing, the objection stops the recording for that country.

**Telephony facts to confirm**
- Microsoft's telephony number pages list all eight countries. Local calling numbers are confirmed in the pages for Ireland, France and Canada. The pages for the UK, the USA, Spain, South Africa and Australia still need checking for calling capability and for inbound calling restrictions.
- Number availability depends on the Azure subscription's billing location, so the subscription structure must be checked before ordering numbers.

**Confirming test**
In each launch country, place and receive real calls through the baseline, record them, and verify: number availability and any registration or documentation needed; announcement and key-press consent behaviour in the other party's language; audio quality and channel separation (each party on its own channel is expected); transcription accuracy on narrowband telephone audio against the evaluation set; behaviour when recording cannot start; and storage in the intended region.

**Overturned if**
Numbers cannot be obtained in a launch country, Legal does not accept the consent approach there, or a required AI service is not available in the chosen region. The fallback for that country is import only (tier 3).

**Also needed**
- Announcement wording in each language, reviewed by Legal.
- A decision on whether a logged objection flags the item for review.
- The retention period for call recordings and call details.
- A permission for viewing phone numbers, since they are personal data.

## Dependencies outside engineering

- Legal: consent wording, the objection policy, Meet's announcement, residency, the AI provider's terms, and the jurisdiction table for telephone calls in each launch country, including announcement wording and number presentation rules.
- Security: key management, private networking, and the audit retention period.
- Business: the supported languages, retention periods for video and transcripts, and the company size limit for attachments.

## Phase 0 exit criteria

Phase 0 is complete when:
1. Every decision above is Accepted or has a named alternative chosen.
2. The evaluation set has been run and the results are recorded against the agreed targets.
3. The AI provider terms and the consent approach are signed off by Legal.
4. The Teams bot has recorded a test meeting end to end.
5. The thin vertical slice (decision 03) runs in the chosen region.
6. Telephony: numbers, consent behaviour and recording are verified in each launch country, or that country is moved to import only (decision 15).

## Parked for later phases

Voice enrolment for speaker identification, purge in Meet, enhanced playback, Widget on iOS, Meet integrations beyond the first conferencing platform, regional carrier adapters (tier 2) and showing users' own mobile numbers, and countries beyond the first eight.
