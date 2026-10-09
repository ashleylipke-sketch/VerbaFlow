# VerbaFlow: notes for Claude

Read this first, then `README.md` and `docs/`. Keep this file current: update it when decisions or status change.

## What this is
One platform made of three apps, built from a blank canvas:
- **Speak**: Dictate, Speech and Meeting modes in one app. **Meeting mode is built.** Dictate and Speech are not started.
- **Meet**: calendar-driven recorder. Not started.
- **Widget**: system-wide dictation overlay. Not started.

Repo: `ashleylipke-sketch/VerbaFlow` (GitHub). Decisions already made by the owner: build the foundation and Speak Meeting mode first, code lives in this repo, **stand-ins now, Azure later** (every Azure service sits behind an interface in `VerbaFlow.Core/Providers`).

The owner is not a developer. Explain in plain words, give exact commands, never ask them to paste a secret/key into chat, and say plainly when something is untested.

## Stack and layout
.NET 10 (Core, Infrastructure, Api, Tests), minimal API, SQLite standing in for Azure SQL (JSON document tables plus a hash-chained append-only audit table), React 19 + Vite 7 + TypeScript in `web/` (built into `src/VerbaFlow.Api/wwwroot`, git-ignored), vitest, Playwright for browser checks.
- `src/VerbaFlow.Core`: domain, permissions, audit chain, transcripts, provider interfaces. No dependencies.
- `src/VerbaFlow.Infrastructure`: SQLite, media store, services, Azure adapters (`Azure/`), stand-ins (`StandIns/`), `Services/FfmpegAudioEnhancer.cs`.
- `src/VerbaFlow.Api`: endpoints, dev auth, background worker.
- `docs/`: decision record, data model, and one setup guide per Azure service.

## Rules that must not be broken (all enforced and tested)
- Approval locks the item; the database enforces it with triggers (JSON key is camelCase `$.status`).
- Reopen needs two different admins.
- Audit chain is SHA-256, append-only. Media is write-once.
- Permission order: lock, then role, then admin and grants.
- Speaker renames are NOT tracked as transcript changes. Edits are tracked word by word against machine version 1 (original struck through until approved).
- Original recordings are never altered. Only a processing copy is cleaned up.

## Status
Built and confirmed working by the owner on real Azure: record or import, Azure AI Speech transcription with speakers, repeated-speaker merging, follow-along word highlighting, tracked changes, custom vocabulary, Azure OpenAI summary/action points/minutes/tone (deployment `gpt-4.1-mini`), regenerate button, Author column in the list, save/discard after recording.
Built, **not yet confirmed by the owner**: ffmpeg audio clean-up (mono 16 kHz, levelled) and custom vocabulary with mixed en+fr locales.

## Speaker separation (in progress: needs the owner's real-meeting result)
Azure's own diarization failed on a 4 or 5 person meeting recorded in-app on one microphone: it first over-split one person, and after the ffmpeg audio clean-up it found only 2 speakers and missed the 4th. The owner chose to run a **separate local speaker-separation model** (option 2) and combine its output with the Azure transcript. The owner declined manual reassign/merge tools as the main fix.
Built: `SherpaSpeakerDiarizer` (sherpa-onnx, Pyannote segmentation 3.0 + TitaNet-small, models auto-downloaded into `data/models` and checksum-pinned), `SpeakerAligner` (each Azure word goes to the local speaker with most overlap, phrases are split at voice changes, lone stray words are smoothed), wired into `ProcessingService` with fallback to Azure's labels plus a warning. Settings `Diarization:Threshold` (default 0.8), `Diarization:NumSpeakers`, `Diarization:Enabled`. See `docs/speaker-separation.md`.
**Owner's 5-person English meeting, one microphone, titanet-small at 0.8:** found 5 then 6 speakers for 5 people. One man was split across 3 labels (Speaker 1, 2 and 6); two people (the 5th and Speaker 5) were merged into one label; one text box held a woman's and a man's words (likely a phrase with no usable word timings being given to one speaker; now estimated and split); the quiet person was missed when people talked over each other (Azure returned no words there). Added `Diarization:EmbeddingModel` (titanet-small default, titanet-large, wespeaker-resnet34, wespeaker-resnet34-lm). Next: owner retries with `titanet-large` at 0.8, then 0.9, on a fresh import/recording and reports speakers found and wrong-person sentences. Not yet verified that a stronger model helps on the owner's audio.
Verified only on a public four-speaker sample (Chinese, finds 4 at threshold 0.8; 0.5 gave 8). **Not yet verified on the owner's English/French meeting.** Next step: the owner records the 4 or 5 person meeting again and reports how many speakers it found; then tune the threshold, or expose NumSpeakers on the Record/Import pages. Other options if this fails: Azure batch transcription with a minimum speaker count, one microphone per person, manual speaker-fix tools.

## Languages
Record and Import have a **Language spoken** choice (English, French, English and French, or "Not sure: detect automatically", which sends no locales so Azure uses its multilingual model; Azure says specifying the locale is faster and more accurate). It is stored on the item (`SpokenLanguages`, default for pre-existing items is both) and sent to Azure as the locale list. One language is faster and more accurate than two; the old behaviour of always sending both likely contributed to Azure 408 timeouts on a 3:48 recording. A failed item's Retry has the same choice. Summary language follows (French only gives a French summary, otherwise British English).

## Requested by the owner, for the next release
1. **Live sound wave while recording** (Record page) so the user can see the app is hearing them. Use the Web Audio analyser on the microphone stream; keep it light and respect reduced-motion.
2. **Pinned play/pause bar** on the item page, so the player stays visible (sticky top or bottom) while following the transcript during playback.

## Not built yet (parked)
Dictate and Speech modes, Meet, Widget, telephony bridge (ADR-15), purge/retention, attachments, export/signing, noise filtering, Entra ID sign-in, Azure SQL/Blob/Service Bus/Defender adapters, automatic voice-to-person matching.

## Running things
Owner on Windows: `run.cmd` in the repo root (builds web, starts http://localhost:5044). If `dotnet` is not found in a plain Command Prompt: `set PATH=C:\Program Files\dotnet;%PATH%` (a 32-bit dotnet can come first on PATH). Secrets are set with `dotnet user-secrets` from `src\VerbaFlow.Api`: `Speech:Endpoint`, `Speech:Key`, `OpenAI:Endpoint`, `OpenAI:Key`, `OpenAI:Deployment`, optional `Audio:FfmpegPath`. ffmpeg: `winget install Gyan.FFmpeg`. Startup log lines say whether Speech, Summaries and Audio are real or stand-in.
Dev sign-in is an `X-Dev-User` header (alice, bob, carol (admin), dave (admin), sam (VerbaFlow support: sees the Support: errors page and `/api/support/errors`, and deliberately no customer items)); it stands in for Entra ID.

Tests: `dotnet test` (158 pass) and `cd web && npx vitest run` (13 pass). Always build, run the tests, and check UI changes in a browser (Playwright, Chromium) before committing.

## Working agreements
- **Customers never see technical error text, including their administrators.** They get a plain sentence plus a reference code (VF-XXXXXX). Technical detail goes to the support-only error table and the server log under the same code. Pass failures through `FailureReporter` (see `docs/support-and-errors.md`). Do not put `ex.Message`, provider responses or setup advice (keys, pricing tiers) in anything a customer can read.
- Commit and push after each finished piece. Commit messages end with the attribution lines the session gives you.
- Never commit keys. Keep docs and README status in step with the code.
- Treat transcript text as untrusted when sending it to an AI model.
- Say plainly what has and has not been tested against real Azure.
