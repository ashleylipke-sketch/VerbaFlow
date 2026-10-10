# Local speaker separation

Azure AI Speech turns audio into words, but its own speaker labels can merge or split voices, especially with several people on one microphone. VerbaFlow therefore also runs a speaker-separation model **on your own PC**. It listens to the audio only to work out *who spoke when*. Each word Azure transcribed is then given to whoever was speaking at that moment, so a phrase is split where the voice changes.

Nothing extra is sent to the cloud. If the local step fails, the app falls back to Azure's own speaker labels and records a warning in the item's history.

## What you need

- ffmpeg installed (the same one used for audio preparation, see `audio-preparation.md`).
- Internet access the **first time only**. The app downloads two small open models (about 46 MB) into `src\VerbaFlow.Api\data\models`, and checks each against a fixed checksum before using it. It starts the download when the app starts. The startup log says `Speakers: models are ready` when done.

Nothing else to install. There is no Python and no account.

The models are Pyannote segmentation 3.0 (finds where voices change) and NeMo TitaNet-small (recognises voices), run through the open-source sherpa-onnx library.

## Check it is on

The startup log says one of:
- `Speakers: using the local speaker-separation model ...`
- `Speakers: using Azure's own speaker labels. Local speaker separation needs ffmpeg ...`

A processed transcript's engine name ends in `+sherpa-onnx:pyannote-3.0+titanet-small`, and the item's history shows `speakerSeparation`.

## Tuning

It works out the number of speakers by itself. Two optional settings (from `src\VerbaFlow.Api`, then restart):

```
dotnet user-secrets set "Diarization:Threshold" "0.8"
dotnet user-secrets set "Diarization:NumSpeakers" "4"
```

- **Threshold** (default 0.8). Higher gives fewer speakers, lower gives more. If one person is split into two, raise it (0.85, 0.9). If two people are merged into one, lower it (0.7, 0.6).
- **NumSpeakers** (default: not set). If you always know the exact number of people, set it. It overrides the threshold. Remove it for meetings with a different number of people: `dotnet user-secrets remove "Diarization:NumSpeakers"`.
- **EmbeddingModel** (default `titanet-small`). The model that recognises voices. Others you can choose: `titanet-large` (bigger, more discriminating, same threshold scale), `wespeaker-resnet34` and `wespeaker-resnet34-lm` (a different family; these need a much **lower** threshold, around 0.3 to 0.4, and give 1 speaker at 0.8). Each model is downloaded on first use and checked against a fixed checksum. Example: `dotnet user-secrets set "Diarization:EmbeddingModel" "titanet-large"`.
- **Audio** (default `prepared`). Which audio the model listens to when telling voices apart: `prepared` is the levelled copy, `original` is the untouched recording. Levelling can distort voices, so if voices are being mixed up, try `dotnet user-secrets set "Diarization:Audio" "original"`, restart and use **Run again**. History shows `voicesHeardFrom`.
- **MinSpeakerSeconds** (default 8). With the number of speakers left automatic, any voice group that spoke for less than this in total is folded into the nearest real speaker (the model turns coughs, laughs and changes of voice into extra "people"). History shows `smallVoicesFolded`; `localSpeakersFound` and `localVoiceSeconds` stay raw, before folding. Set to 0 to switch it off. A number the owner gives with **Run again** is trusted and nothing is folded. A very quiet real participant who speaks for under 8 seconds in total would be folded in too, so lower it for short meetings.
- To switch the feature off: `dotnet user-secrets set "Diarization:Enabled" "false"`.

## Running it again on the same recording

On an item page, **Speakers look wrong?** lets the owner (or an admin) run the conversion again on the same audio, optionally saying how many people spoke (2 to 10). It is offered until the item is approved. If the transcript already has saved corrections, the page says how many and asks for confirmation, because running again replaces them with a fresh transcript; the History records `replacedEdits`. The History records `processing.rerun_requested` with the count, and the next `processing.completed` line shows `localSpeakersFound`, `localVoiceTurns`, `localVoiceSeconds` (how long each voice group spoke, longest first, so a tiny group stands out) and `speakersToldTo`, so it is clear what the local model did. If the local model finds no voices at all, the app keeps Azure's own labels (`speakerSeparation: azure`). **Audio to use** on the same card lets that run skip the audio clean-up and use the original recording instead (see `audio-preparation.md`); History shows `audioSentToSpeech`.

## The short-window method

The model's own grouping can give one label to several voices. **Way of telling voices apart: Short-window** on the same card uses a second method. The model is still used to find where people are talking; then each stretch of speech is cut into overlapping 1.5 second windows, each window gets its own voice fingerprint (same voice model, `Diarization:EmbeddingModel`), and the fingerprints are grouped (average-linkage clustering, cosine distance, `VoiceClusterer`). With no count given, the number of speakers is read from the biggest jump in merge distance (between 2 and 8); with a count, it makes exactly that many groups. A lone odd window between two windows of the same voice is smoothed away (`WindowedTurns`). Small groups are still folded by `MinSpeakerSeconds` when no count is given. History shows `groupingMethod`. To make it the default for every recording: `dotnet user-secrets set "Diarization:Method" "windowed"`.

Tested: the grouping logic with unit tests, and on the public four-speaker sample, where told 4 it agrees with the normal method on over 90% of the speech. **Not yet tested on the owner's English meetings.**

## Speech service only

**Way of telling voices apart: Speech service only** skips the local model for that run. The number of speakers you pick is sent to Azure as its speaker limit (it is an upper limit, not an exact count; with automatic it is 8, as before), and Azure's own labels are used as they come back. History shows `groupingMethod: azure`. Added because the local model put almost all speech in one group on the owner's one-microphone meeting. On the owner's 4-person one-microphone meeting it returned exactly 4 speakers and was mostly right (a few text boxes still mixed people; some quiet background speech was not transcribed at all, which no labelling method can fix). Record and Import have an optional **Number of speakers** box; giving a count there uses this method from the start.

## How well it works

Checked automatically on a public four-person sample recording (in Chinese), where it finds all four voices at the default threshold. Different threshold values gave 4 to 8 speakers on the same recording, so **the threshold matters and the right value depends on your audio**. It has not been measured on English or French meetings with five people on one microphone. Try it, and tell the developers how many voices it found against how many there were.

Measured on the four-speaker sample (speakers found at thresholds 0.4 / 0.5 / 0.6 / 0.7 / 0.8 / 0.9): titanet-small 8 / 8 / 6 / 5 / 4 / 4; titanet-large 9 / 7 / 5 / 5 / 4 / 4; wespeaker-resnet34 4 / 2 / 1 / 1 / 1 / 1. That recording is only 10 turns and not English, so it shows how each model's threshold scale differs, not which is better on your meetings.

If a phrase arrives from Azure without usable word timings, the words are spread evenly across the phrase so a change of voice inside it can still be split (the timings are then a guess and are not kept).

Speed: roughly one minute of processing for every ten minutes of audio on a normal laptop CPU.
