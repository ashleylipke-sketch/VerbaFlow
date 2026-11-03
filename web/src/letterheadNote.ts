import type { Letterhead } from './api';

/** Says whether a dictation's export will be on the letterhead, and if not, why. */
export function letterheadNote(documentType: string | null, l: Letterhead | null): string {
  if (documentType !== 'letter' && documentType !== 'document' && documentType !== 'email')
    return 'This dictation is not marked as a letter, Word document or email (it was made before that choice), so it is exported as plain text.';
  if (!l) return 'Goes on the letterhead.';
  if (l.word && l.pdf) return 'Goes on the letterhead, in Word and in PDF.';
  if (l.word) return 'Word goes on the letterhead. No PDF letterhead is uploaded yet, so a PDF is plain.';
  if (l.pdf) return 'PDF goes on the letterhead. No Word letterhead is uploaded yet, so a Word file is plain.';
  return 'No letterhead is uploaded yet (an admin adds one on the Letterhead page), so it is exported on a plain page.';
}
