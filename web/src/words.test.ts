import { describe, expect, it } from 'vitest';
import { activeSegmentIndex, activeWordIndex, wordsFor } from './words';

const timed = { text: 'Manchester United win', startMs: 1000, endMs: 4000,
  words: [{ text: 'Mannchester', startMs: 1000, endMs: 2000 }, { text: 'United', startMs: 2000, endMs: 3000 }, { text: 'win', startMs: 3000, endMs: 4000 }] };

describe('wordsFor', () => {
  it('uses the speech timings but shows the corrected spelling', () => {
    const w = wordsFor(timed);
    expect(w.map(x => x.text)).toEqual(['Manchester', 'United', 'win']);
    expect(w.map(x => x.startMs)).toEqual([1000, 2000, 3000]);
  });
  it('spreads words across the passage when timings are missing or no longer match', () => {
    const w = wordsFor({ ...timed, text: 'Manchester United will win', words: null });
    expect(w).toHaveLength(4);
    expect(w[0].startMs).toBe(1000);
    expect(w[3].endMs).toBe(4000);
    for (let i = 1; i < w.length; i++) expect(w[i].startMs).toBe(w[i - 1].endMs);
    expect(wordsFor({ ...timed, text: 'one two' })).toHaveLength(2); // stale timings with a different count
  });
  it('longer words get more time', () => {
    const w = wordsFor({ text: 'a extraordinarily', startMs: 0, endMs: 1000, words: null });
    expect(w[1].endMs - w[1].startMs).toBeGreaterThan(w[0].endMs - w[0].startMs);
  });
  it('handles empty text', () => { expect(wordsFor({ text: '  ', startMs: 0, endMs: 10, words: null })).toEqual([]); });
});

describe('activeWordIndex', () => {
  const w = wordsFor(timed);
  it('is -1 before the first word, then follows the last word that started', () => {
    expect(activeWordIndex(w, 500)).toBe(-1);
    expect(activeWordIndex(w, 1000)).toBe(0);
    expect(activeWordIndex(w, 2999)).toBe(1);
    expect(activeWordIndex(w, 3000)).toBe(2);
    expect(activeWordIndex(w, 99999)).toBe(2);
  });
});

describe('activeSegmentIndex', () => {
  const segs = [{ text: 'a', startMs: 1000, endMs: 3000, words: null }, { text: 'b', startMs: 8000, endMs: 9000, words: null }];
  it('finds the passage being read, and none during silence', () => {
    expect(activeSegmentIndex(segs, 0)).toBe(-1);
    expect(activeSegmentIndex(segs, 2000)).toBe(0);
    expect(activeSegmentIndex(segs, 3500)).toBe(0);      // short linger
    expect(activeSegmentIndex(segs, 5000)).toBe(-1);     // long gap
    expect(activeSegmentIndex(segs, 8000)).toBe(1);
    expect(activeSegmentIndex(segs, 20000)).toBe(-1);
  });
});
