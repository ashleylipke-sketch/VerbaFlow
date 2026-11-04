# Audio preparation for better speaker separation

Before a recording goes to Azure AI Speech, VerbaFlow makes a **processing copy** of the audio and sends that copy. The original recording is never changed and stays the record.

The copy is made:
- **mono**, because speaker separation works on one channel;
- **16 kHz**, which is what speech models are built for;
- with **low rumble removed** (below 80 Hz);
- with the **loudness evened out**, so a quiet speaker at the far end of a room is not drowned out by a loud one next to the microphone. Speakers who sound very quiet are the most likely to be missed or merged into someone else.

The browser recorder also asks the microphone for mono, echo cancellation, noise suppression and automatic gain.

This improves the input Azure's speaker separation sees. It cannot make Azure's model perfect, and several people on a single microphone remains the hardest case.

## Reducing background noise (a choice per run)

Hum, fans, traffic, keyboards and chatter in the background can make the speech service mishear or drop words. On an item page, **Audio to use** (on the **Speakers or language look wrong?** card) offers two more choices:

- **Cleaned up, less background noise**: the noise filter's result is mixed with 40% of the untouched sound, which protects quiet words.
- **Cleaned up, much less background noise**: only the filtered sound. Takes away the most noise, but can also take away very quiet or distant voices.

The filter is RNNoise (ffmpeg's `arnndn`), a small model trained to keep speech and remove everything else. Its model file ("somnolent-hogwash", 2018, from GregorR/rnnoise-models, not subject to copyright) is built into VerbaFlow, so nothing is downloaded. Noise is taken away first, then the loudness is evened out, so the levelling does not lift the noise again. As with all clean-up, only the processing copy is filtered; the recording you hear and download is never changed.

Filtering can hurt as well as help (speech services are trained on everyday noisy audio), so it is not on by default. To find out which works best on your recordings, run the same noisy recording again with each choice and compare. The History shows `audio: less-noise` or `much-less-noise` on the run request, and on the finished run `audioSentToSpeech` ("prepared, background noise reduced a little / a lot") and `audioPrep`, which names the model.

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

## Comparing with the original recording

On an item page, the **Speakers or language look wrong?** card has **Audio to use**. Choose **Original recording, no clean-up** and press **Run again** to have the speech service (and the local speaker model) hear the untouched recording for that run. Run again with **Cleaned up (normal)** to go back. This is how to check whether the clean-up helps or hurts on your own audio. Running again replaces any saved corrections on that transcript (the page asks first), so compare on a recording you have not corrected.

The History shows `audio: original` on the run request and `audioSentToSpeech: original` on the finished run.

Each finished item records `audioPrep` in its history ("mono 16 kHz, levelled", the same with the background noise choice, or "none"). If the clean-up was skipped, `audioPrepWarning` says why. A skipped clean-up never stops transcription.
