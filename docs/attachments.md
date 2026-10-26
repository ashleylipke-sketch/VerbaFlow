# Attached files

Any meeting, dictation or Speech item can keep files with it: the agenda, board papers, slides, the letter being answered.
The **Attached files** card is on the item page.

- **Who adds and removes:** whoever can edit the text (the author or importer; the assignee once they have accepted it).
  Approval locks the files with the rest; reopening the recording (two administrators) unlocks them again.
- **Who opens them:** everyone who can open the recording (author, assignee, people the meeting was also for, administrators).
  PDFs and pictures open in a new tab; other files download.
- **Versions:** files belong to the whole recording, so a reopened version keeps them. Files are added on the newest version.
- **What can be attached:** PDF, Word (.docx, .doc), Excel (.xlsx, .xls), PowerPoint (.pptx, .ppt), OpenDocument, RTF,
  text, CSV, pictures (PNG, JPEG, GIF, WebP) and emails (.msg, .eml); up to 25 MB each and 20 per recording.
  Refused: programs, files with macros (.docm, .xlsm, .pptm, or a .docx carrying macros), and files whose content does not
  match their name (for example a program renamed to .pdf). Every file goes through the virus scan.
- **Stored:** write-once, with its SHA-256 fingerprint, under `data/media/<attachment id>/`.
- **History:** `attachment.added` (name, size, fingerprint), `attachment.downloaded`, `attachment.removed`.
- **Deleting:** deleting the recording (by hand, by two administrators, or by the retention rules) deletes its files
  too (`item.deleted` says how many). The retention rule that removes only the sound or video keeps them.
- **Exports:** the Word and PDF exports list the attached files' names under the details ("kept in VerbaFlow");
  the files themselves are not put inside the export or attached to emails.

Code: `Core/Domain/Attachment.cs`, `Infrastructure/Services/AttachmentService.cs`, endpoints in
`Api/RetentionEndpoints.cs` (`GET/POST /items/{id}/attachments`, `GET/DELETE /items/{id}/attachments/{attachmentId}`),
`web/src/AttachmentsCard.tsx`. Table `doc_attachments`.
