import type { Letterhead } from './api';

/** Says whether a dictation's export will be on the letterhead, and if not, why. */
export function letterheadNote(documentType: string | null, l: Letterhead | null): string {
  if (documentType !== 'letter')
    return `Only letters go on the letterhead. This dictation is ${documentType === 'email' ? 'an email' : documentType === 'document' ? 'a Word document' : 'not marked as a letter (it was made before the Letter / Word document / Email choice)'}, so it is exported as plain text.`;
  if (!l) return 'Letters go on the letterhead.';
  if (l.word && l.pdf) return 'Goes on the letterhead, in Word and in PDF.';
  if (l.word) return 'Word goes on the letterhead. No PDF letterhead is uploaded yet, so a PDF is plain.';
  if (l.pdf) return 'PDF goes on the letterhead. No Word letterhead is uploaded yet, so a Word file is plain.';
  return 'No letterhead is uploaded yet (an admin adds one on the Letterhead page), so the letter is plain.';
}
