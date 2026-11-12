# South African languages through Google

Azure AI Speech cannot transcribe most of South Africa's official languages. Six of them now go to **Google
Speech-to-Text** (Google's servers in the EU) instead:

| Language | Google model | Words wrong in the trial (public recordings) |
|---|---|---|
| isiXhosa | chirp_3 | 35% (FLEURS) |
| Northern Sotho (Sepedi) | chirp_3 | 32% (FLEURS) |
| Setswana | long | 32% |
| siSwati | long | 8% (short prompts, possibly heard by Google before) |
| Xitsonga | long | 10% (same) |
| Tshivenda | long | 24% (same) |

**Not offered:** Sesotho (Google got 62% of words wrong, so it stays switched off) and isiNdebele (no speech service
has it). English, Afrikaans and isiZulu stay on Azure as before.

## What is different for these languages

- **One language per recording.** A Google language cannot be mixed with English or Afrikaans in the same recording
  (Azure and Google cannot share one recording). The picker shows "(only language)" next to them and hides
  "+ Another language" once one is chosen.
- **No live text.** They are not offered in Speech mode or the Widgets. Use Dictate (record, then converted) or
  Meeting instead.
- **Speakers.** Google gives no speaker labels for these languages. In a meeting, VerbaFlow's own voice model works out
  who spoke when (also when you gave a number of speakers). That model was unreliable on one-microphone meetings
  before, so expect to fix speakers with Change speaker and Join.
- **Custom vocabulary** is not sent to Google.
- **Accuracy is lower than English.** The scores above are on clean read speech; real conversation will be worse.
  Nobody at VerbaFlow can read these languages, so a native speaker should check the first real results.
- Long recordings are sent in pieces of under a minute (Google's limit for a quick request), cut at the quietest
  moment, so processing takes a little longer. History shows `googleSpeech`, for example
  "Google chirp_3, 12 pieces".
- Cost: Google charges per minute of audio sent (see Google's Speech-to-Text pricing page; not checked here).

## Setting it up on your computer (Windows)

You need the Google Cloud project used for the language trial (`cs-project-0xzelowp`, named "development"), where the
Speech-to-Text API is already switched on. VerbaFlow signs in to Google **as you**, through Google's own command-line
tool, so no key file is needed (your organisation does not allow key files anyway).

1. Install the Google Cloud command-line tool. In a Command Prompt:

       winget install Google.CloudSDK

   Close the Command Prompt and open a new one.
2. Sign in (a browser window opens; choose the Google account that owns the project and allow access):

       gcloud auth application-default login

3. Tell Google which project pays for it:

       gcloud auth application-default set-quota-project cs-project-0xzelowp

4. Tell VerbaFlow to use Google for these languages:

       cd src\VerbaFlow.Api
       dotnet user-secrets set GoogleSpeech:Project cs-project-0xzelowp

5. Start VerbaFlow with `run.cmd`. The start-up messages should include a line beginning
   **South African languages: isiXhosa, Sepedi, … through Google Speech-to-Text**. If it says
   "Google speech NOT used", the rest of that line says why.

Steps 2 and 3 save a sign-in file in `%APPDATA%\gcloud`. VerbaFlow reads it at start-up. If Google later stops
accepting it (for example after you change your Google password), run step 2 again and restart VerbaFlow.

ffmpeg must be installed (it already is if the audio clean-up works).

### Other settings

- `GoogleSpeech:Location`: `eu` (default). Google's EU servers are the only ones checked for these languages.
- `GoogleSpeech:CredentialsFile`: a credentials file somewhere else, for example a service account key
  (`"type": "service_account"`) on a server where keys are allowed. Without it, the file named by
  `GOOGLE_APPLICATION_CREDENTIALS` or the gcloud sign-in above is used.

### When VerbaFlow runs online

Not set up yet. Online, VerbaFlow cannot use your personal sign-in; it needs either a service account key (blocked by
your organisation's policy) or keyless sign-in from Azure to Google (workload identity federation, as GitHub does for
the trial), which is not built yet.

## Checks

- Tests: `SouthAfricanLanguageTests` (pieces under a minute cut at a quiet moment, times moved back, sign-in with a
  gcloud file and with a service account key, retry without word timings, refusals, meeting speakers from the local
  voice model, dictations skip word for word, live text refused).
- **Real check, 12 November 2026** (workflow **Google speech check**, about 90 seconds of native speakers' read speech
  each, through VerbaFlow's own code): isiXhosa (chirp_3, FLEURS) 32% of words wrong, with word timings; Setswana
  (long, OpenSLR) 23% wrong, but Google gave **no word timings and no punctuation** for Setswana, so follow-along
  highlighting is estimated and the text has no full stops (the same is expected for siSwati, Xitsonga and Tshivenda,
  which use the same model; not checked). Both recordings were cut into 2 pieces and the times reached the end.
- GitHub workflow **Google speech check** sends about 90 seconds of real isiXhosa (FLEURS) and Setswana (OpenSLR)
  speech through VerbaFlow's own code to Google's EU servers and shows the results on the run's summary page.
