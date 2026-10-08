# Connect Azure AI Speech (real transcription)

VerbaFlow uses placeholder text until it is given an Azure AI Speech resource. With one connected, recordings and imports are transcribed with speaker labels and language detection (English and French candidates for now).

## 1. Create the resource (about 5 minutes)

1. Sign in at https://portal.azure.com.
2. **Create a resource**, search for **Speech**, choose **Speech** (Azure AI services), then **Create**.
3. Pick a subscription and resource group, a region (for example **UK South**), a name, and the **Standard S0** tier. (The Free F0 tier also works for small tests.)
4. When it is created, open the resource and go to **Resource Management, Keys and Endpoint**.
5. Copy **KEY 1** and the **Endpoint**. The endpoint looks like `https://<your-name>.cognitiveservices.azure.com/`.

Check that your region supports fast transcription: https://learn.microsoft.com/azure/ai-services/speech-service/regions

## 2. Give VerbaFlow the key (kept out of the code)

Run these in Command Prompt from the `src\VerbaFlow.Api` folder:

```
dotnet user-secrets set "Speech:Endpoint" "https://<your-name>.cognitiveservices.azure.com/"
dotnet user-secrets set "Speech:Key" "<KEY 1>"
```

User secrets are stored on your PC, outside the repository, so the key is never committed. Do not paste the key into any file in the repo, into a chat, or into an issue. If it leaks, regenerate it in the portal (Keys and Endpoint).

## 3. Run and check

Start the app with `run.cmd`. The first lines it prints say which speech engine is in use:

- `Speech: using Azure AI Speech at ...` means real transcription is on.
- `Speech: STAND-IN (placeholder text)...` means the endpoint or key was not found.

Record or import a meeting. The transcript page shows the engine name under the heading. Real transcripts say `azure-ai-speech-fast-transcription-...`.

## Notes

- Limits: files under 500 MB and 5 hours.
- Cost: Azure bills per audio hour transcribed. Check current pricing for your tier.
- Audio is sent to Azure to be transcribed. Production should use a Speech resource in the region your data-protection rules require, and keyless Microsoft Entra authentication instead of a key.
- The summary, minutes and action points come from Azure OpenAI. See `docs/azure-openai-setup.md`.

## Custom vocabulary

Administrators keep a shared list of names and terms on the **Vocabulary** page. It is sent to Azure with every recording or import transcribed from then on, which makes those words more likely to be heard correctly. It nudges the service but cannot force a word, so transcripts still need checking.

- Anyone signed in can read the list; only administrators change it. Every change is in the audit trail.
- Keep it short and focused. Azure advises under 2,000 terms and says longer lists lower quality and slow things down. VerbaFlow stops at 2,000.
- Terms are sent to Azure along with the audio.
- Each transcription records how many terms were used. If Azure rejects the list, the recording is transcribed without it and the record says so.
- Azure documents the vocabulary feature for one language at a time. VerbaFlow transcribes English and French together, so this has not been confirmed against live Azure. The fallback above covers it if Azure refuses.
