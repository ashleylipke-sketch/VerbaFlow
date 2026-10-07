# VerbaFlow

One platform for three apps: **Speak** (Dictate, Speech and Meeting modes), **Meet** (calendar-driven recorder) and **Widget** (system-wide dictation overlay).

## Status

| Area | State |
|---|---|
| Speak · Meeting mode | **Built** (record, import, stand-in transcription, assign / accept / return, approve and lock, two-admin reopen, versioned transcript, audit chain) |
| Speak · Dictate, Speech | Not started |
| Meet, Widget | Not started |
| Telephony bridge (ADR-15) | Designed, not built |
| Azure services | Stand-ins in place; adapters not written |

## What is real and what is a stand-in

Everything marked *stand-in* sits behind an interface in `VerbaFlow.Core/Providers` and is replaced by the Azure service without touching the workflow code.

| Stand-in | Replaced by |
|---|---|
| `X-Dev-User` header sign-in (`DevUsers.cs`) | Microsoft Entra ID |
| SQLite | Azure SQL |
| Local media folder, read-only files | Blob storage with immutability policy |
| `StandInSpeechService` (fake text) | Azure AI Speech (diarization, language detection) |
| `StandInAiOutputService` | Azure OpenAI |
| `StandInMalwareScanner` (rejects EICAR) | Defender for Storage |
| In-process queue | Service Bus |

The rules that matter are real, not stand-ins: permissions, the approval lock (enforced by database triggers), append-only hash-chained audit, write-once media with SHA-256, and the two-admin reopen.

## Run it

```bash
dotnet test                       # 72 tests
cd web && npm install && npm run build   # builds into src/VerbaFlow.Api/wwwroot
cd ../src/VerbaFlow.Api && dotnet run     # http://localhost:5044
```

Pick a user at the top right: Alice (author), Bob (assignee), Carol and Dave (admins). Data is kept in `src/VerbaFlow.Api/data/`.

## Layout

- `src/VerbaFlow.Core`: domain, permissions, audit chain, transcripts, provider interfaces. No dependencies.
- `src/VerbaFlow.Infrastructure`: SQLite, media store, services, stand-ins.
- `src/VerbaFlow.Api`: minimal API.
- `web/`: React + TypeScript front end.
- `docs/`: decision record and data model.
