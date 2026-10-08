# Audio preparation for better speaker separation

Before a recording goes to Azure AI Speech, VerbaFlow makes a **processing copy** of the audio and sends that copy. The original recording is never changed and stays the record.

The copy is made:
- **mono**, because speaker separation works on one channel;
- **16 kHz**, which is what speech models are built for;
- with **low rumble removed** (below 80 Hz);
- with the **loudness evened out**, so a quiet speaker at the far end of a room is not drowned out by a loud one next to the microphone. Speakers who sound very quiet are the most likely to be missed or merged into someone else.

The browser recorder also asks the microphone for mono, echo cancellation, noise suppression and automatic gain.

This improves the input Azure's speaker separation sees. It cannot make Azure's model perfect, and several people on a single microphone remains the hardest case.

## Install ffmpeg (Windows)

Open a Command Prompt and run:

```
winget install Gyan.FFmpeg
```

Close the Command Prompt, open a new one and check with `ffmpeg -version`. Then run `run.cmd`.

If `ffmpeg` is installed somewhere that is not on the PATH, tell the app where it is:

```
dotnet user-secrets set "Audio:FfmpegPath" "C:\tools\ffmpeg\bin\ffmpeg.exe"
```

(run from `src\VerbaFlow.Api`).

## Check it is on

The startup log says one of:
- `Audio: ffmpeg found, so recordings are made mono, 16 kHz and levelled before transcription`
- `Audio: ffmpeg NOT found, so recordings are sent as they are.`

Each finished item records `audioPrep` in its history ("mono 16 kHz, levelled" or "none"). If the clean-up was skipped, `audioPrepWarning` says why. A skipped clean-up never stops transcription.
