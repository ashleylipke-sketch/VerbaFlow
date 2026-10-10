# VerbaFlow

One platform for three apps: **Speak** (Dictate, Speech and Meeting modes), **Meet** (calendar-driven recorder) and **Widget** (system-wide dictation overlay).

## Status

| Area | State |
|---|---|
| Speak · Meeting mode | **Built** (record, import, real transcription with speakers, follow-along word highlighting, tracked changes, custom vocabulary, assign / accept / return, approve and lock, two-admin reopen, delete (two admins once approved), versioned transcript, audit chain) |
| Speak · Dictate, Speech | Not started |
| Meet, Widget | Not started |
| Telephony bridge (ADR-15) | Designed, not built |
| Azure services | Speech and OpenAI (summary, action points, minutes, tone) adapters built, each needs your key. ffmpeg audio clean-up built. SQL, Blob, Service Bus, Defender, Entra ID not written |

## What is real and what is a stand-in

Everything marked *stand-in* sits behind an interface in `VerbaFlow.Core/Providers` and is replaced by the Azure service without touching the workflow code.

| Stand-in | Replaced by |
|---|---|
| `X-Dev-User` header sign-in (`DevUsers.cs`) | Microsoft Entra ID |
| SQLite | Azure SQL |
| Local media folder, read-only files | Blob storage with immutability policy |
| `StandInSpeechService` (fake text) | **Azure AI Speech: built.** Used automatically when `Speech:Endpoint` and `Speech:Key` are set. See `docs/azure-speech-setup.md` |
| `StandInAiOutputService` | **Azure OpenAI: built.** Used automatically when `OpenAI:Endpoint`, `OpenAI:Key` and `OpenAI:Deployment` are set. See `docs/azure-openai-setup.md` |
| `StandInAudioEnhancer` (no change) | **ffmpeg clean-up: built.** Used automatically when ffmpeg is installed. See `docs/audio-preparation.md` |
| Azure's own speaker labels | **Local speaker separation: built** (sherpa-onnx, runs on your PC). See `docs/speaker-separation.md` |
| `StandInMalwareScanner` (rejects EICAR) | Defender for Storage |
| In-process queue | Service Bus |

The rules that matter are real, not stand-ins: permissions, the approval lock (enforced by database triggers), append-only hash-chained audit, write-once media with SHA-256, and the two-admin reopen.

## Run it

**Windows (easiest):** double-click `run.cmd` in the repo folder, or run it from Command Prompt. It checks that .NET 10 and Node.js are installed, builds the web app and starts the server at http://localhost:5044. Keep the window open while you use the app.

**Manual steps:**

```bash
dotnet test                       # 143 tests
cd web && npm install && npm run build   # builds into src/VerbaFlow.Api/wwwroot
cd ../src/VerbaFlow.Api && dotnet run     # http://localhost:5044
```

Pick a user at the top right: Alice (author), Bob (assignee), Carol and Dave (admins). Data is kept in `src/VerbaFlow.Api/data/`.

## Layout

- `src/VerbaFlow.Core`: domain, permissions, audit chain, transcripts, provider interfaces. No dependencies.
- `src/VerbaFlow.Infrastructure`: SQLite, media store, services, stand-ins.
- `src/VerbaFlow.Api`: minimal API.
- `web/`: React + TypeScript front end.
- `docs/`: decision record, data model, setup guides, and `support-and-errors.md` (what customers see when something fails versus what support sees).
