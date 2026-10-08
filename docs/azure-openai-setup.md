# Azure OpenAI: summary, action points, minutes and tone

With this connected, each transcript gets a real summary, action points, minutes and a tone note beside it. Without it the app shows clearly labelled placeholder text.

## 1. Create the resource and a model deployment

1. In the Azure portal, create an **Azure OpenAI** resource (or an Azure AI Foundry resource). Choose the region your data-protection rules require.
2. Open **Azure AI Foundry** from the resource, go to **Deployments**, and deploy a chat model that supports structured outputs (for example a GPT-4o or GPT-4.1 family model).
3. Note the **deployment name** you give it. This is the name you chose, not the model's own name.
4. On the resource, open **Keys and Endpoint** and copy the endpoint (like `https://your-name.openai.azure.com/`) and one key.

## 2. Give the app the details

In a Command Prompt, in the `src\VerbaFlow.Api` folder (add `set PATH=C:\Program Files\dotnet;%PATH%` first if `dotnet` is not found):

```
dotnet user-secrets set "OpenAI:Endpoint" "https://your-name.openai.azure.com/"
dotnet user-secrets set "OpenAI:Key" "paste-the-key-here"
dotnet user-secrets set "OpenAI:Deployment" "your-deployment-name"
```

The key is stored outside the repository. Never paste it into a chat, a file in the repository, or a commit.

## 3. Check it

Run `run.cmd`. The startup log should say `Summaries: using Azure OpenAI deployment ...`. Record or import a meeting: the Summary panel names the engine (`azure-openai:your-deployment-name`).

## How it behaves

- The summary is written in the item's output language (British English or French).
- It uses only what is in the transcript, names people as they appear in the transcript, and judges tone from the words only.
- Text spoken in a meeting is treated as data. Instructions inside a transcript are not followed.
- **Regenerate from current transcript** rewrites the summary after you correct the transcript or rename speakers. Approved (locked) items cannot be regenerated.
- If the summary fails (wrong key, busy service, content filter), the transcript is still delivered. The item shows "No summary yet" and a **Create summary** button. The failure is recorded in the history.
- Very long meetings are summarised in parts and then combined.

## Notes

- Cost: billed per token. Check current pricing for your model.
- The transcript text is sent to Azure OpenAI. Audio is not. Production should use keyless Microsoft Entra authentication instead of a key.
