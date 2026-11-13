# Saved voices ("Your voice")

People can save a short recording of their voice once. After that, meetings recorded in a room put their name on what
they said instead of "Speaker 1", with no introductions needed.

## Saving your voice

1. Open **More ▾** (or **Settings ▾** for admins) → **Your voice**.
2. Press **Start** and read the text on the page in your normal voice for at least 20 seconds. Any language is fine.
   It stops by itself after 90 seconds. Use the microphone you use for meetings, in a quiet place.
3. Press **Stop**, listen to it if you like, tick the box to agree, and press **Save my voice**.

You can **Listen to it**, **Record it again** or **Remove my voice** on the same page at any time. Nobody else can
see, hear or change your saved voice, administrators included.

## When it is used

- Meetings recorded or imported in VerbaFlow, by anyone in your organisation (a personal account's voice is used only
  for that person's own meetings).
- Not when the meeting started with introductions (those name people instead), not for online meetings recorded by the
  note-taker (Teams, Zoom and the others already give names), and not for dictations.
- A meeting recorded before someone saved their voice is named from it when it is run again (**Speakers look wrong?** on
  the meeting's page).

How it works: each speaker's sentences are put through VerbaFlow's voice model, averaged into one voice fingerprint per
speaker, and compared with the saved voices. A speaker gets a person's name only when they sound clearly like that
person and clearly more like them than anyone else; each person names one speaker at most. Speakers who match nobody
keep "Speaker 1", "Speaker 2" and so on.

History on the meeting shows `savedVoices`, for example "3 saved voices compared with 4 speakers; 2 named from them"
(counts only, never names), and the speech engine ends with `+saved-voices`.

## Limits

- It needs the local speaker-separation model (the start-up line **Saved voices:** says whether it is on).
- Voice recognition can be wrong, particularly with one microphone in a busy room, where the local voice model did badly
  on earlier test meetings. Check the names and fix them with Rename or Change speaker.
- It names whole speakers as the speech service grouped them. If the speech service put two people in one speaker, that
  speaker gets the name of whoever spoke most.

## Personal data

A voice fingerprint is personal (biometric) data. VerbaFlow keeps it only after the person ticks the box to agree, keeps
the recording and fingerprint in the data folder (`data/media/voices` and the `doc_voice_profiles` table), and deletes
both when the person presses Remove my voice or their account is removed. History records only that a voice was saved or
removed (`voice.saved`, `voice.removed`), never the recording or fingerprint. Before using this with real staff, check
with whoever handles data protection in your organisation whether a privacy notice needs to mention it.
