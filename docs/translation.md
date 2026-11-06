# Translating a recording

A **Translate** card on the page of a meeting, dictation or Speech item makes a machine translation of its text into another language. For a meeting it also translates the summary, action points, minutes and tone. The recording itself never changes.

## Making a translation
1. Open the recording.
2. In the **Translate** card (on the right, above the Summary), choose a language under **Translate into…**, then press **Translate**.
3. Press **Open** to read it. The page shows the original and the translation side by side; on a phone, the translation sits under the original.

You can translate into English, Afrikaans, Arabic, Chinese (Simplified), Dutch, French, German, Italian, Japanese, Polish, Russian, Spanish and Ukrainian, with up to 6 languages per recording. These are the languages that exports also print their headings and dates in.

## Who can do what
- **Making and removing** a translation: anyone who can export the recording (the owner, administrators, department admins, and the assignee or attendees once it is approved). Approved recordings can be translated too, because the recording itself does not change.
- **Reading** a translation: anyone who can open the recording.

## Exporting a translation
On the translation's page, or in the recording's **Export ▾** menu, choose the language under **Language**, then use Download, Preview, Email or Share as usual.
- Headings, labels and dates are printed in that language. The details table gets a line saying it is a machine translation that no person has checked, and that the original is the official record.
- For letters and emails, the line sits at the top with the Draft line.
- The approval marker and the digital signature are the recording's own.
- The file name ends with the language code, for example `Board meeting (approved, fr).pdf`.
- A letter's or email's sign-off closing ("Yours sincerely,") is translated as well. The name and job title stay as they are.

## When the text changes
A translation is made from the text as it was at that moment. If someone edits the transcript or regenerates the summary afterwards, the card says "The text has changed since this was translated". It cannot be exported until you press **Update translation**.

## Behind the scenes
- The translation uses Azure OpenAI, with the same settings and deployment as summaries and Check grammar. Each translation is a small charge.
- Without Azure OpenAI set up, a stand-in puts `[fr]` in front of each line so the pages can be tried.
- The text is sent as untrusted data: it is translated, never followed.
- History records `translation.made` (language, engine, size), `translation.removed` and `translation.failed` (with a VF- reference). It never records the words themselves.
- Translations are deleted when the recording is deleted.
