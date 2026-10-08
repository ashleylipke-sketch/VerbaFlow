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
- To switch the feature off: `dotnet user-secrets set "Diarization:Enabled" "false"`.

## How well it works

Checked automatically on a public four-person sample recording (in Chinese), where it finds all four voices at the default threshold. Different threshold values gave 4 to 8 speakers on the same recording, so **the threshold matters and the right value depends on your audio**. It has not been measured on English or French meetings with five people on one microphone. Try it, and tell the developers how many voices it found against how many there were.

Speed: roughly one minute of processing for every ten minutes of audio on a normal laptop CPU.
