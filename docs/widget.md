# Widget: speaking into any website

First version, built 2026-10-13 (ADR 12: browser extension first). **Not yet tried by the owner. Not tried on real Azure through the extension, in Microsoft Edge itself, or on real sites such as Outlook on the web or Gmail.** Checked in Chromium with a fake microphone and the stand-in speech service on a test page (plain box, text area, rich editor, password and card boxes).

## Install (once per computer)
1. Edge: `edge://extensions` (Chrome: `chrome://extensions`), switch on **Developer mode**.
2. **Load unpacked**, choose `VerbaFlow\widget\extension`.
3. The settings page opens: **Allow the microphone**, VerbaFlow address (default `http://localhost:5044`), who you are (dev sign-in stand-in), language, **Save and check**.

The VerbaFlow app's **Widget** page repeats these steps.

The listening is done by a hidden extension page, not the settings page. If the browser's microphone question is answered "Allow this time", only the settings page is allowed and the Widget says it may not use the microphone (owner saw this in Edge, 2026-10-14). The settings page now checks through the hidden page itself and, when it is refused, offers **Open the microphone setting** (the browser's site settings for the extension), where Microphone is set to Allow.

## Using it
- Click in a text box: a microphone button appears in its corner (or **Alt+Shift+D**).
- **Red**: listening; words appear live (grey while still being heard). **Stop**.
- **Amber**: the words in an editable box; **Accept** (Ctrl+Enter), **Speak more**, **Cancel** (Escape).
- **Green**: the words are put in the box where the cursor was (a space is added when needed), using the browser's own typing so the website notices the change and its undo works.
- After Accept a plain one-line box or text area is **locked** (read-only, green outline); double-click it to change the whole text by voice or by hand. Rich editors (email bodies) are not locked, because locking them would break the website's editor.
- Select text first, then click the microphone, to replace only that part.
- Spoken commands, saved text and custom vocabulary work as in Dictate (the live session is the same as Speech mode's).
- Never offered in password boxes, boxes marked for card numbers, card codes or one-time codes, or boxes whose name or label suggests them.

## Owner's first real use (2026-10-14, Chrome, Outlook web, real Azure)
Worked: live text in the email body, "new line" after "Dear George,", and "insert disclaimer 1.1" put the saved disclaimer in. Two problems, both fixed and **not yet confirmed**:
- An email address said in the To box came out as "Ashley dot Lipke @btinternet.com". Now a spoken address becomes one word (ashley.lipke@btinternet.com) in every dictation: words joined by spoken "dot", "underscore" or "dash" before an "@" (or a spoken "at" after a spoken "dot"), with a domain containing a dot. "find it at example.com" stays as said. History `commandsHeard` "email address joined".
- After stopping to correct the text and choosing Speak more, "colon new paragraph" did nothing: each Speak more is a new listen, and a command said before any word had nothing to attach to. Now a comma, colon and the like said first is kept and joins the text already there, and the live messages say `startsWithBreak` so the Widget puts a paragraph break after the existing text.

## Correcting by voice (owner asked 2026-10-14; unit-tested only, untested on real Azure)
Works in the Widget, Speech mode and Dictate.
- **"scratch that"** removes the last phrase (the words since the comma, full stop or other punctuation before it), or a whole piece of saved text if that came last. "Dear George comma I hope this finds you scratch that I hope you are well" gives "Dear George, I hope you are well".
- **"replace Tuesday with Thursday"** or **"correct Tuesday to Thursday"** changes the last "Tuesday" in the same paragraph. The old words can be up to 6; the new words end at a pause, a command or a sentence end, so pause briefly after them. Capitals and punctuation around the old words are kept. If the old words are not found, everything stays as said (History `commandsHeard` "replace, words not found").
- "delete that" is not a command (too common in ordinary speech).

## Second real use (2026-10-21, Chrome, Outlook web)
"scratch that" worked. Built after it, **not yet confirmed**:
- "new paragraph" only moved to the next line, because Outlook's paragraphs have no space between them. In a rich editor the Widget now leaves an empty line between paragraphs, unless the editor already puts 6 px or more between them.
- **Fix grammar** in the check step (amber): the text in the box is sent line by line to the grammar checker (Azure OpenAI, the same as Dictate's Check grammar), and the corrected text replaces it; **Undo fix** puts it back. Lines that are saved text, list markers and blank lines are left alone; a suggestion that rewrites the line (keeps under 60% of the wording) is left out. `POST /api/widget/{id}/grammar` {text}; History `widget.grammar_checked` (counts only) or `widget.grammar_failed` (VF- reference). One small Azure OpenAI charge per use.
- "I hope this e-mail finds you" came out twice without "well"; the next try heard "well", so nothing was changed.
- **Owner confirmed Fix grammar works on real Azure OpenAI (2026-10-21).**

## What is kept
Nothing the person said. The live session is thrown away on Accept or Cancel (and when the tab closes). VerbaFlow keeps:
- `doc_widget_sessions`: user, site (host only, e.g. `mail.example.com`), browser and system (e.g. "Edge on Windows"), languages, start, end, outcome (accepted/cancelled), number of listens, seconds spoken, number of words, reconnects.
- `doc_widget_policy`: websites where administrators switched the Widget off (a site covers its sub-sites; at most 500).
- Audit rows with product `widget` and no item: `widget.started`, `widget.accepted`, `widget.cancelled`, `widget.policy_changed`. `WidgetService.RequireContentFree` refuses any Widget audit entry with a key outside a fixed list or a value that could carry text.

## Server
`POST /api/widget/start` {site, device, spokenLanguages} gives a live session and ticket (refused on a blocked site); `POST /api/widget/{id}/listen-again`; `POST /api/widget/{id}/end` {outcome, spokenMs, words, reconnects}; `GET`/`PUT /api/widget/policy` {blockedSites} (only admins change); `GET /api/widget/sessions` (your own; admins see everyone's). The audio goes over the same WebSocket as Speech mode (`/api/live/{id}/socket`).

## Extension (`widget/extension`, Manifest V3)
`background.js` (service worker: which box is listening, policy cache of 5 minutes, relays messages), `offscreen.html`/`offscreen.js`/`worklet.js` (microphone with processing off, 16 kHz PCM, WebSocket, reconnect), `content.js` (button and panel in a closed shadow root, insertion, lock), `options.html`/`options.js` (settings), `settings.js` (calls to VerbaFlow with the `X-Dev-User` header, the stand-in for Entra ID sign-in).

## Not built
Other apps outside the browser (Word, Outlook desktop), Entra ID sign-in in the extension, store publishing, admin-pushed install (Edge/Chrome policy), blocking by page rather than site.
