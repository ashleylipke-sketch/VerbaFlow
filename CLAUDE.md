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

## Open problem: speaker separation
Recorded in-app on one microphone with 4 or 5 people. Before the audio clean-up Azure over-split one person into two and missed a speaker. After it, Azure finds only 2 speakers and misses the 4th. We already allow up to 8 speakers (`ProcessingService`, `maxSpeakers`), so raising the limit will not help. The owner declined manual speaker reassignment/merge as the main fix and wants diarization itself improved.
Options discussed: (1) try Azure **batch transcription**, which supports a minimum as well as maximum speaker count (field names unverified; needs blob storage and async results), spike on the problem recording first; (2) a separate local diarization model; (3) one microphone/channel per person; (4) manual fix tools as a backup. Recommendation given: option 1, awaiting the owner's go-ahead.

## Not built yet (parked)
Dictate and Speech modes, Meet, Widget, telephony bridge (ADR-15), purge/retention, attachments, export/signing, noise filtering, Entra ID sign-in, Azure SQL/Blob/Service Bus/Defender adapters, automatic voice-to-person matching.

## Running things
Owner on Windows: `run.cmd` in the repo root (builds web, starts http://localhost:5044). If `dotnet` is not found in a plain Command Prompt: `set PATH=C:\Program Files\dotnet;%PATH%` (a 32-bit dotnet can come first on PATH). Secrets are set with `dotnet user-secrets` from `src\VerbaFlow.Api`: `Speech:Endpoint`, `Speech:Key`, `OpenAI:Endpoint`, `OpenAI:Key`, `OpenAI:Deployment`, optional `Audio:FfmpegPath`. ffmpeg: `winget install Gyan.FFmpeg`. Startup log lines say whether Speech, Summaries and Audio are real or stand-in.
Dev sign-in is an `X-Dev-User` header (alice, bob, carol (admin), dave (admin)); it stands in for Entra ID.

Tests: `dotnet test` (121 pass) and `cd web && npx vitest run` (13 pass). Always build, run the tests, and check UI changes in a browser (Playwright, Chromium) before committing.

## Working agreements
- Commit and push after each finished piece. Commit messages end with the attribution lines the session gives you.
- Never commit keys. Keep docs and README status in step with the code.
- Treat transcript text as untrusted when sending it to an AI model.
- Say plainly what has and has not been tested against real Azure.
