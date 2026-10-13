# Speech mode: text typed as you speak

**Status:** built 2026-10-13. Tried only with the stand-in (placeholder words) in Chromium. **Not yet tried on real Azure.**

## What it does

Choose **Speech** at the top, then **Start speaking**. Choose Letter, Word document or Email, give it a name (and a client reference if you like) and the language you will speak, then press **Start speaking**.

The text appears on the page as you speak, laid out as the letter, document or email. Words still being worked out are shown lighter and settle within a second or so. The spoken commands from Dictate all work: "comma", "full stop", "new paragraph", "bullet point", "insert legal 1.1" and the rest.

Press **Stop** when you are done, then **Save**. The item then works like a dictation: listen and follow along, edit, assign to a typist, approve, export on the letterhead.

## The recording is kept

Everything the microphone hears is kept as the recording (made 16 kHz mono, nothing else done to it), exactly as with Dictate. The text heard live is used as the item's text. If the live text is incomplete for any reason, the text is worked out again from the recording when you save, so nothing is lost.

## A poor connection

If the connection drops while you speak, keep going: the recording carries on in the browser, the page says the connection was lost, and it reconnects by itself. When it is back, the part you said meanwhile is sent again and the text catches up. If the connection never comes back before you save, the text is worked out from the recording instead.

## What the History shows

- `speech.live`: how many phrases were heard live, whether the live text finished, how many times the connection was made again (`reconnects`), and how far the text was behind your voice (`textDelayMedianMs`, the usual delay, and `textDelayWorstMs`, the longest), in thousandths of a second.
- `processing.completed`: `speechServiceForDictation: live` and `liveText: used (...)` when the live text became the item's text, or `liveText: not used: ...` with the reason.

## How it works (for developers)

The browser sends the microphone as 16 kHz, 16-bit mono PCM over a WebSocket to VerbaFlow (`/api/live/{id}/socket`), which passes it to Azure AI Speech real-time recognition (`AzureLiveSpeech`, Speech SDK push stream, the same settings as the word-for-word dictation route). Your Speech key stays on the server. With several languages chosen, Azure's continuous language identification picks the language phrase by phrase (untested). See CLAUDE.md, "Speech mode", for the detail.
