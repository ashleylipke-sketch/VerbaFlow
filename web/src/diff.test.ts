import { describe, expect, it } from 'vitest';
import { diffWords } from './diff';

const show = (a: string, b: string) => diffWords(a, b).map(o => (o.kind === 'same' ? o.text : o.kind === 'del' ? `-${o.text}` : `+${o.text}`)).join(' ');

describe('diffWords', () => {
  it('marks nothing when the text is identical', () => {
    expect(diffWords('a b c', 'a b c').every(o => o.kind === 'same')).toBe(true);
  });
  it('shows a corrected word as removed then added', () => {
    expect(show('Mannchester United win', 'Manchester United win')).toBe('-Mannchester +Manchester United win');
  });
  it('shows inserted and deleted words', () => {
    expect(show('United win', 'Manchester United will win')).toBe('+Manchester United +will win');
    expect(show('Manchester United will win', 'United win')).toBe('-Manchester United -will win');
  });
  it('puts removed words before their replacements in a changed stretch', () => {
    expect(show('the quick brown fox', 'the slow red fox')).toBe('the -quick -brown +slow +red fox');
  });
  it('treats a changed capital or punctuation mark as a change', () => {
    expect(show('hello there', 'Hello there.')).toBe('-hello -there +Hello +there.');
  });
  it('gives each current word its index, so audio timings can be attached', () => {
    const ops = diffWords('a b c', 'a x c');
    expect(ops.filter(o => o.kind !== 'del').map(o => o.cur)).toEqual([0, 1, 2]);
    expect(ops.find(o => o.kind === 'del')!.cur).toBeUndefined();
  });
  it('copes with empty text and with huge passages', () => {
    expect(show('', 'new words')).toBe('+new +words');
    expect(show('old', '')).toBe('-old');
    const big = Array.from({ length: 600 }, (_, i) => `w${i}`).join(' ');
    const ops = diffWords(big, big + ' end');
    expect(ops.length).toBeGreaterThan(0);
    expect(ops.filter(o => o.kind === 'ins').length).toBeGreaterThan(0);
  });
});
