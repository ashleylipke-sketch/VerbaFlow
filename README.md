# VerbaFlow

One platform for three apps: **Speak** (Dictate, Speech and Meeting modes), **Meet** (calendar-driven recorder) and **Widget** (system-wide dictation overlay).

## Status

| Area | State |
|---|---|
| Speak · Meeting mode | **Built** (record, import, real transcription with speakers, follow-along word highlighting, tracked changes, custom vocabulary, assign / accept / return, approve and lock, two-admin reopen, delete (two admins once approved), export to Word and PDF with preview, email and fingerprint check, versioned transcript, audit chain) |
| Speak · Dictate | **Built (first version)**: dictate or import, convert to text as one voice with spoken punctuation ("comma", "full stop", "new paragraph"), edit, Check grammar (suggestions you accept or reject), assign to a typist, approve, export the text |
| Speak · Speech | **Built (first version; first real-Azure run worked)**: the text is typed as you speak, with the same spoken commands as Dictate; the recording is kept; a lost connection catches up; save as a Draft and carry on later; list views as agreed (Draft, Speech to Text, Awaiting/With Assignee, Conversion Failed, Completed, Outstanding Tasks). See `docs/speech-mode.md` |
| Widget | Browser extension for Edge/Chrome (`widget/extension`, see `docs/widget.md`); works on real Azure in Outlook on the web. **Widget for Windows** (`widget/windows`, install with `widget-windows.cmd`, see `docs/widget-windows.md`) for Outlook, Word, Teams and any program: built, not yet run on Windows |
| Meet | Not started |
| Telephony bridge (ADR-15) | Designed, not built |
| Running online | **Built, not yet run in Azure**: one container (Azure Container Apps), PostgreSQL, an Azure Files share for recordings, set up by one script in Azure Cloud Shell. See `docs/going-online.md` |
| Azure services | Speech and OpenAI (summary, action points, minutes, tone) adapters built, each needs your key. ffmpeg audio clean-up built. Entra ID sign-in built and confirmed. PostgreSQL built for online use. Blob immutability, Service Bus, Defender not written |

## What is real and what is a stand-in

Everything marked *stand-in* sits behind an interface in `VerbaFlow.Core/Providers` and is replaced by the Azure service without touching the workflow code.

| Stand-in | Replaced by |
|---|---|
| Test users through the `X-Dev-User` header (`DevUsers.cs`, Development only) | **Microsoft Entra ID sign-in: built, confirmed by the owner (2026-10-23).** Used when `Entra:TenantId` and `Entra:ClientId` are set. See `docs/entra-sign-in-setup.md` |
| SQLite (on one computer) | **PostgreSQL: built** (`PostgresDatabase`, same rules by triggers), used when `Database:ConnectionString` is set; the online setup uses Azure Database for PostgreSQL |
| Local media folder, read-only files | Online: an Azure Files share mounted at `/data`. Later: Blob storage with an immutability policy |
| `StandInLiveSpeech` (Speech mode: one placeholder word per 0.4 s) | **Azure AI Speech real-time recognition: built, confirmed on real Azure.** Used when `Speech:Endpoint` and `Speech:Key` are set |
| `StandInSpeechService` (fake text) | **Azure AI Speech: built.** Used automatically when `Speech:Endpoint` and `Speech:Key` are set. See `docs/azure-speech-setup.md` |
| `StandInAiOutputService` | **Azure OpenAI: built.** Used automatically when `OpenAI:Endpoint`, `OpenAI:Key` and `OpenAI:Deployment` are set. See `docs/azure-openai-setup.md` |
| `StandInEmailSender` (saves .eml files in `data/outbox`) | **Azure Communication Services Email: built, untested on real Azure.** Used when `Email:ConnectionString` and `Email:Sender` are set. See `docs/azure-email-setup.md` |
| `StandInAudioEnhancer` (no change) | **ffmpeg clean-up: built.** Used automatically when ffmpeg is installed. See `docs/audio-preparation.md` |
| Azure's own speaker labels | **Local speaker separation: built** (sherpa-onnx, runs on your PC). See `docs/speaker-separation.md` |
| `StandInMalwareScanner` (rejects EICAR) | Defender for Storage |
| In-process queue | Service Bus |

The rules that matter are real, not stand-ins: permissions, the approval lock (enforced by database triggers), append-only hash-chained audit, write-once media with SHA-256, and the two-admin reopen.

## Run it

**Windows (easiest):** double-click `run.cmd` in the repo folder, or run it from Command Prompt. It checks that .NET 10 and Node.js are installed, builds the web app and starts the server at http://localhost:5044. Keep the window open while you use the app.

**Manual steps:**

```bash
dotnet test                       # 387 tests (set VERBAFLOW_TEST_POSTGRES to run them on PostgreSQL)
cd web && npm install && npm run build   # builds into src/VerbaFlow.Api/wwwroot
cd ../src/VerbaFlow.Api && dotnet run     # http://localhost:5044
```

Pick a test user at the top right: Alice (author), Bob (assignee), Carol and Dave (admins), or sign in with Microsoft once it is set up (`docs/entra-sign-in-setup.md`). Data is kept in `src/VerbaFlow.Api/data/`.

## Layout

- `src/VerbaFlow.Core`: domain, permissions, audit chain, transcripts, provider interfaces. No dependencies.
- `src/VerbaFlow.Infrastructure`: SQLite and PostgreSQL, media store, services, stand-ins.
- `Dockerfile`, `deploy/`: running online (`docs/going-online.md`).
- `src/VerbaFlow.Api`: minimal API.
- `web/`: React + TypeScript front end.
- `docs/`: decision record, data model, setup guides, and `support-and-errors.md` (what customers see when something fails versus what support sees).
