# Data model: items, transcripts and permissions

**Scope:** Speak, Meet and Widget on one shared model | **Status:** Proposed, for review

## Principles

1. **One item model for Speak and Meet.** A mode and a product tag distinguish them. Statuses are stored once as internal codes, and each mode maps them to its own labels.
2. **Approved records never change.** Reopening creates a new linked item version. The original stays locked.
3. **Transcripts are immutable segments plus versions.** An edit creates a new segment and a new version, never an overwrite. Machine version 1 always survives.
4. **Original media is write-once and hashed.** Filtered copies are separate records and are deleted after approval.
5. **Audit is append-only and hash-chained.** Widget writes to it without content, and has no content tables at all.
6. **Locks beat grants.** An admin grant can add a capability, but it can never override the approval lock.

## 1. Items and workflow

```mermaid
erDiagram
  ITEM_CHAIN ||--|{ ITEM : versions
  USER ||--o{ ITEM : owns
  ITEM ||--o{ ASSIGNMENT : history
  ITEM ||--o{ MEDIA_ASSET : has
  ITEM ||--o{ ATTACHMENT : has
  ITEM ||--o{ EVENT_MARKER : timeline
  ITEM ||--o| MEETING_SESSION : meet_only
  ITEM ||--o| CALL_DETAIL : phone_call_only
  CALL_DETAIL ||--|{ RECORDING_PART : parts
  JURISDICTION_RULE ||--o{ CALL_DETAIL : applied
  ITEM ||--o{ REOPEN_REQUEST : requests
  REOPEN_REQUEST ||--o{ REOPEN_APPROVAL : needs_two
  ITEM ||--o{ EXPORT : produces
  ITEM ||--o| PURGE_REQUEST : may_have

  ITEM_CHAIN {
    uuid id PK
    string client_reference
  }
  ITEM {
    uuid id PK
    uuid chain_id FK
    int version_no
    uuid supersedes_item_id FK
    bool is_current
    string product "speak or meet"
    string mode "dictate speech meeting meet_recording"
    string source_kind "recorded imported calendar_bot phone_call"
    string status_code
    string name
    string description_type
    string priority
    date due_date
    uuid owner_user_id FK
    uuid assigned_user_id FK
    uuid assigned_department_id FK
    string output_language
    uuid language_library_id FK
    uuid dictionary_id FK
    bool ai_context_enabled
    bool objection_flag
    timestamp approved_at
    uuid approved_by FK
    timestamp locked_at
  }
  ASSIGNMENT {
    uuid id PK
    uuid item_id FK
    uuid to_user_id FK
    uuid to_department_id FK
    string reason "assign sick leave mistake"
    uuid by_user_id FK
    timestamp at
  }
  MEDIA_ASSET {
    uuid id PK
    uuid item_id FK
    string kind "original filtered video enhanced"
    string storage_uri
    string sha256
    int duration_ms
    string provenance "recorded imported bot phone_call"
    json import_metadata
    timestamp deleted_at
  }
  ATTACHMENT {
    uuid id PK
    uuid item_id FK
    uuid added_by FK
    string stage
    string sha256
    string scan_status "quarantined clean rejected unscannable"
    uuid supersedes_id FK
    int linked_offset_ms
    timestamp removed_at
    string removal_reason
  }
  EVENT_MARKER {
    uuid id PK
    uuid item_id FK
    int offset_ms
    string type "snippet objection pause join_point consent_announcement recording_start recording_failure"
    uuid ref_id
  }
  MEETING_SESSION {
    uuid item_id PK
    string platform
    string calendar_event_id
    string join_url
    string session_status "scheduled joining recording processing ready join_failed skipped"
    string skip_reason
    string dedupe_key
  }
  CALL_DETAIL {
    uuid item_id PK
    string direction "inbound outbound"
    string tier "speak_calling carrier_adapter native_import speakerphone"
    string adapter_name
    string user_number
    string other_party_number
    string user_country
    string other_party_country
    string provider_call_id
    timestamp call_connected_at
    timestamp call_ended_at
    int recording_started_offset_ms "how far into the call recording began"
    string consent_mode_applied "announce_implied explicit"
    uuid jurisdiction_rule_id FK
    string announcement_language
    timestamp announcement_completed_at
    timestamp consent_given_at
    bool continue_after_objection
    string storage_region
    bool incomplete
    string channel_map "user on channel 1, other party on channel 2"
  }
  RECORDING_PART {
    uuid id PK
    uuid item_id FK
    int part_no
    uuid media_asset_id FK
    int call_offset_start_ms
    int call_offset_end_ms
    string end_reason "user_stop user_pause failure call_ended"
    string pause_reason
  }
  JURISDICTION_RULE {
    uuid id PK
    string country_code
    string consent_mode "announce_implied explicit no_recording"
    bool continue_after_objection
    string storage_region
    string announcement_language
    string legal_review_status
    date reviewed_on
    int version_no
  }
  REOPEN_REQUEST {
    uuid id PK
    uuid item_id FK
    uuid requested_by FK
    string reason
    string outcome
  }
  REOPEN_APPROVAL {
    uuid id PK
    uuid request_id FK
    uuid admin_user_id FK
    string decision
  }
  EXPORT {
    uuid id PK
    uuid item_id FK
    uuid transcript_version_id FK
    string format
    uuid template_version_id FK
    string marker "draft approved superseded"
    string sha256
    bool signed
    string certificate_thumbprint
    uuid exported_by FK
  }
  PURGE_REQUEST {
    uuid id PK
    uuid item_id FK
    string reason
    uuid approver_one FK
    uuid approver_two FK
    timestamp purged_at
  }
```

**Status codes and labels.** The item stores one internal code, and a mapping table supplies each mode's label.

| Internal code | Dictate | Speech | Meeting | Meet |
|---|---|---|---|---|
| `draft` | Draft | Draft | none | none |
| `converting` | Converting | progress tag | progress tag | Processing |
| `conversion_failed` | Conversion Failed | Conversion Failed | Conversion Failed | Join Failed (join stage) |
| `with_author` | With Author | Speech to Text | With Author (in-app only) | none |
| `with_importer` | none | none | Imported | none |
| `awaiting_assignee` | Awaiting Assignee | Awaiting Assignee | Awaiting Assignee | none |
| `with_assignee` | With Assignee | With Assignee | With Assignee | none |
| `completed` | Completed | Completed | Completed | Ready |
| `purged` | none | none | Purged | none |

Telephone calls recorded by the system use `with_author` like any in-app Meeting recording. Telephone recordings supplied as files use `with_importer`, so the two never mix.

Self-assigned means `assigned_user_id = owner_user_id`. The tag is derived and not stored. Outstanding Tasks (Speech) is a query, not a status.

## 2. Transcripts and AI outputs

```mermaid
erDiagram
  ITEM ||--|| TRANSCRIPT : has
  TRANSCRIPT ||--|{ TRANSCRIPT_VERSION : versions
  TRANSCRIPT_VERSION ||--|{ VERSION_SEGMENT : lists
  SEGMENT ||--o{ VERSION_SEGMENT : reused_by
  ITEM ||--o{ SPEAKER : has
  SPEAKER ||--o{ SEGMENT : speaks
  SEGMENT ||--o| SNIPPET_INSERTION : may_be
  TRANSCRIPT_VERSION ||--o{ TRANSLATION : derived
  ITEM ||--o{ AI_OUTPUT : generates
  ITEM ||--o{ ACTION_ITEM : tracks
  ITEM ||--o{ PROCESSING_RUN : runs

  TRANSCRIPT_VERSION {
    uuid id PK
    uuid transcript_id FK
    int version_no
    string kind "machine_v1 live_draft human_edit speaker_correction"
    uuid parent_version_id FK
    uuid created_by FK
    string capacity "author assignee importer admin"
    uuid processing_run_id FK
    timestamp created_at
  }
  SEGMENT {
    uuid id PK
    uuid speaker_id FK
    string language
    int start_ms
    int end_ms
    string text
    float confidence
    string flags "overlap background low_confidence"
    string source_type "speech snippet"
  }
  VERSION_SEGMENT {
    uuid version_id FK
    int seq
    uuid segment_id FK
  }
  SPEAKER {
    uuid id PK
    uuid item_id FK
    string label
    string display_name
    uuid user_id FK
    string languages
  }
  SNIPPET_INSERTION {
    uuid segment_id PK
    uuid snippet_version_id FK
    string label_text
    int marker_offset_ms
    bool modified_by_admin
    string original_text
    uuid modified_by FK
  }
  TRANSLATION {
    uuid id PK
    uuid transcript_version_id FK
    string target_language
    string engine
    bool stale
    uuid created_by FK
  }
  AI_OUTPUT {
    uuid id PK
    uuid item_id FK
    string type "summary minutes actions tone_meeting tone_speaker"
    string language
    int version_no
    uuid source_version_id FK
    json content
    json attachments_used
    uuid processing_run_id FK
  }
  ACTION_ITEM {
    uuid id PK
    uuid item_id FK
    string text
    uuid owner_user_id FK
    date due_date
    bool done
    string source "ai user"
    uuid ai_output_id FK
  }
  PROCESSING_RUN {
    uuid id PK
    uuid item_id FK
    string type "transcribe outputs translate"
    string provider_model
    string enhancement_setting
    string status
    string failure_reason
    float cost
  }
```

**How editing works.** Editing one passage creates one new `SEGMENT` row and a new `TRANSCRIPT_VERSION` whose `VERSION_SEGMENT` list points at the unchanged old segments plus the new one. Comparing or restoring versions is just comparing lists. Machine version 1 is never modified, and segment timestamps always point into the original media.

**Speaker corrections** (rename, merge, split, reassign) are versions of kind `speaker_correction`. They are also audit events.

**Snippets in the transcript** are segments with `source_type = snippet`. Admin edits change the insertion's text for this document only, keep `original_text` for the strike-through view, and never touch the library snippet. At approval the display shows the final text with a "modified by admin" flag.

**AI outputs** are rows, so regenerating adds a version and keeps the old one. Each records its source transcript version, language, attachments used and model run. Tone analysis never reads attachments or snippet segments.

**Translations** are derived from a specific transcript version. A new version marks them stale. They are always labelled as machine translation.

## 3. Access, libraries and audit

```mermaid
erDiagram
  USER ||--o{ USER_DEPARTMENT : in
  DEPARTMENT ||--o{ USER_DEPARTMENT : has
  ITEM ||--o{ ITEM_PARTICIPANT : involves
  ITEM ||--o{ ITEM_SHARE : shared_with
  USER ||--o{ PERMISSION_GRANT : receives
  SNIPPET_PREFIX ||--o{ SNIPPET : owns
  SNIPPET ||--|{ SNIPPET_VERSION : versions
  DEPARTMENT ||--o{ SNIPPET_PREFIX : may_own
  TEMPLATE ||--|{ TEMPLATE_VERSION : versions
  USER ||--o{ AUDIT_EVENT : acts

  USER {
    uuid id PK
    string entra_object_id
    string default_language
    string status
  }
  DEPARTMENT {
    uuid id PK
    string name
    string entra_group_id
  }
  PERMISSION_GRANT {
    uuid id PK
    string subject_type "user department group"
    uuid subject_id
    string capability "read write reassign export export_audio view_phone_numbers approve admin"
    string scope_type "company department mode"
    uuid scope_id
    timestamp revoked_at
  }
  ITEM_PARTICIPANT {
    uuid item_id FK
    uuid user_id FK
    string role "author importer assignee"
    timestamp from_at
    timestamp to_at
  }
  ITEM_SHARE {
    uuid item_id FK
    string subject_type
    uuid subject_id
    bool can_read
    bool can_write
    bool can_export
  }
  SNIPPET_PREFIX {
    uuid id PK
    string prefix_text
    string owner_type "company department user"
    uuid department_id FK
    bool active
    string sounds_like_key
  }
  SNIPPET {
    uuid id PK
    uuid prefix_id FK
    string number "digits and dots, ends in digit"
    uuid owner_user_id FK
    string label
  }
  SNIPPET_VERSION {
    uuid id PK
    uuid snippet_id FK
    int version_no
    string language
    string body
    uuid created_by FK
  }
  TEMPLATE {
    uuid id PK
    string scope "company department"
    uuid department_id FK
    string format "pdf pptx docx email"
  }
  TEMPLATE_VERSION {
    uuid id PK
    uuid template_id FK
    int version_no
  }
  AUDIT_EVENT {
    bigint seq PK
    timestamp occurred_at
    uuid actor_user_id FK
    string actor_capacity
    string product
    uuid item_id
    uuid chain_id
    string event_type
    json details
    string prev_hash
    string hash
  }
```

**Effective permissions.** Evaluate in this order and stop at the first deny:
1. **Lock.** A completed item is read-only for everyone. Only a new reopened version can change.
2. **Role on this item.** The author or importer has read/write until approval. The assignee has read/write on the transcript and read-only on audio. A Meet owner has full access and shares rights through `ITEM_SHARE`.
3. **Grants.** Add capabilities such as re-assign, export or audio download. Export and audio download are separate capabilities. A grant can remove a right as well as add one.
4. **Otherwise deny.**

Admins act through grants (re-assign, approve, remove attachments, edit an inserted snippet in one document), and each use is audited.

## Telephone calls (Meeting mode)

A recorded telephone call is an `ITEM` with `product = speak`, `mode = meeting`, `source_kind = phone_call`, and one `CALL_DETAIL` row. It follows the Meeting rules: the audio is never edited, the raw original is stored and hashed, and a filtered copy is used only for transcription.

**Per-call recording.** Recording is never automatic. An item exists only if the user switched recording on for that call and the consent step completed. A call that is never recorded creates no item, transcript or call detail.

**Parts and gaps.** A call can have several `RECORDING_PART` rows, each with its own `MEDIA_ASSET`, offsets into the call, and end reason. A pause, a failure or a restart ends one part and starts the next, so gaps are explicit and visible on the timeline. `recording_started_offset_ms` records how far into the call recording began. `incomplete` is set when a part ended through failure.

**Consent and rules.** `JURISDICTION_RULE` holds one current row per country, versioned, with the consent mode, whether recording continues after an objection, the storage region, the announcement language and the Legal review status. For each call the stricter rule of the user's country and the other party's country is applied, and the rule version actually used is stored on `CALL_DETAIL`, so an old call always shows which rule applied. Recording starts only after `announcement_completed_at` (or `consent_given_at` for explicit mode) is set.

**Failures.** If recording cannot start, no item is created. A content-free audit event records the attempt, a reason code and the user's choice (continue without recording, retry, or end call). Continuing never bypasses the consent step.

**Diarization.** `channel_map` records which party is on which channel. Where channels are separate, speaker labels come from the channel ("You" and "Other party"), and normal diarization is applied only within a channel that carries several people.

**Phone numbers.** Numbers in `CALL_DETAIL` are personal data. They are hidden unless the user holds the `view_phone_numbers` capability, and views are audited. Exports omit them unless the template and the user's rights include them.

**Storage region.** `storage_region` is set at call start from the rule. Media for that call is written only to that region, and the processing queue routes work to services in the same region.

## Integrity rules enforced in the database

- Segments, transcript versions, media hashes and audit events have no update or delete permission for the application.
- Reopen approvals need two different admins, and neither can be the requester.
- Snippet labels must match the number rule (digits and dots, ending in a digit). Prefixes are unique per owner. The reserved User prefix is only valid for user-owned snippets, and uniqueness there is per user.
- Retired prefixes block new snippets but leave existing ones and past documents intact.
- Every insertion stores the snippet version used, so later library edits never change old documents.
- Approved items reject changes by trigger, and the only route to change is a reopen that creates a new `ITEM`.
- Audit events chain by hash. An independent job verifies the chain and the exports to immutable storage.
- A `phone_call` item cannot be created without a completed consent step recorded on its `CALL_DETAIL`, and its `RECORDING_PART` rows are write-once like other media.
- Jurisdiction rules are versioned, never edited in place, and a call keeps a reference to the version it used.
- Phone numbers are readable only through the `view_phone_numbers` capability, and every read is audited.
- Media for a call must be stored in the region named on its `CALL_DETAIL`.

## Widget

Widget has no item, transcript or media tables. It writes `AUDIT_EVENT` rows with `product = widget` and no `item_id`. Two small tables sit beside them: `WIDGET_SESSION` (user, device, app or site, start, stop) and `WIDGET_POLICY` (blocked apps and sites, set by admins). A schema check rejects any Widget event whose details contain text or file names.

## Retention and purge

A `RETENTION_POLICY` per scope and media type (original audio, video, filtered copy, transcript, attachments) drives lifecycle rules. A purge deletes content, keeps the item row as a tombstone (reference, dates, who, why), and writes an audit event.

## Open points

1. **Multiple authors or organisers.** Meet items with several attendees are modelled as one item plus shares. Confirm that is enough.
2. **Action items in Speak.** They share one table with Meet. Confirm Speak needs the same add/edit rights.
3. **Voice enrolment.** Not modelled yet. It needs a separate consent and biometric-data design.
4. **Row-level security.** Whether to enforce the permission order in the database as well as in the application.
5. **Tier 2 carrier adapters.** The model has `tier` and `adapter_name`, but the ingestion contract (what each carrier delivers, and any tamper-evidence it supplies) is defined per adapter later.
6. **Retention for calls.** Retention periods for call recordings and for `CALL_DETAIL` (especially phone numbers) need setting in the retention policy.
7. **Objection handling on calls.** Where a country does not allow recording to continue after an objection, confirm the exact behaviour: stop the part, end the call, or notify the user only.
