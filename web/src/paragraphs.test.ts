import { describe, expect, it } from 'vitest';
import { breaksBefore, paragraphMarkers, paragraphPieces } from './paragraphs';

describe('paragraphs of a dictation', () => {
  it('finds line breaks typed inside a passage', () => {
    expect(breaksBefore('One two.\nThree\n\n four')).toEqual([false, false, true, true]);
    expect(breaksBefore('\nLeading')).toEqual([false]);
  });
  it('joins passages until one starts a paragraph, and splits at typed breaks', () => {
    const p = paragraphPieces([
      { text: 'Dear John,', newParagraph: true }, { text: 'thanks.\nWe agree.', newParagraph: false },
      { text: 'Bye.', newParagraph: true }]);
    expect(p).toEqual([
      [{ seg: 0, from: 0, to: 2 }, { seg: 1, from: 0, to: 1 }],
      [{ seg: 1, from: 1, to: 3 }],
      [{ seg: 2, from: 0, to: 1 }]]);
  });
  it('shows each passage on its own for older dictations', () => {
    expect(paragraphPieces([{ text: 'a', newParagraph: null }, { text: 'b', newParagraph: null }]).length).toBe(2);
  });
  it('keeps an emptied passage with the paragraph before, so its removed words can still be shown', () => {
    const p = paragraphPieces([{ text: 'one two', newParagraph: null }, { text: '', newParagraph: null }, { text: 'three', newParagraph: null }]);
    expect(p).toEqual([[{ seg: 0, from: 0, to: 2 }, { seg: 1, from: 0, to: 0 }], [{ seg: 2, from: 0, to: 1 }]]);
  });
});

describe('paragraphMarkers', () => {
  it('bullets each point and counts numbered points afresh after other paragraphs', () => {
    const segs = [
      { text: 'Please bring:', newParagraph: true, list: null },
      { text: 'Your passport', newParagraph: true, list: 'bullet' },
      { text: 'Sign', newParagraph: true, list: 'number' },
      { text: 'and date it', newParagraph: false, list: null },
      { text: 'Pay', newParagraph: true, list: 'number' },
      { text: 'Thanks', newParagraph: true, list: null },
      { text: 'Again', newParagraph: true, list: 'number' },
    ];
    expect(paragraphMarkers(segs, paragraphPieces(segs))).toEqual([null, '•', '1.', '2.', null, '1.']);
  });
});
