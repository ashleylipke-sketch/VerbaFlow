import { describe, expect, it } from 'vitest';
import { letterheadNote } from './letterheadNote';
import type { Letterhead } from './api';

const file = { sha256: 'a', fileName: 'x', sizeBytes: 1, uploadedBy: 'Carol', uploadedAt: '' };
const lh = (word: boolean, pdf: boolean): Letterhead =>
  ({ word: word ? file : null, pdf: pdf ? file : null, pdfTopFirstMm: 55, pdfTopNextMm: 25, pdfBottomMm: 30, pdfSideMm: 25 });

describe('letterheadNote', () => {
  it('says why a dictation that is not a letter is plain', () => {
    expect(letterheadNote('document', lh(true, true))).toContain('a Word document');
    expect(letterheadNote(null, lh(true, true))).toContain('not marked as a letter');
  });
  it('says which formats go on the letterhead', () => {
    expect(letterheadNote('letter', lh(true, true))).toBe('Goes on the letterhead, in Word and in PDF.');
    expect(letterheadNote('letter', lh(false, true))).toContain('No Word letterhead');
    expect(letterheadNote('letter', lh(true, false))).toContain('No PDF letterhead');
    expect(letterheadNote('letter', lh(false, false))).toContain('No letterhead is uploaded yet');
  });
});
