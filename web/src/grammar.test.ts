import { describe, expect, it } from 'vitest';
import { changes, compose, hunks } from './grammar';

describe('grammar suggestions', () => {
  const before = 'dear mr smith i am writing to you about you\'re letter';
  const after = 'Dear Mr Smith, I am writing to you about your letter.';
  const hs = hunks(before, after);

  it('splits a suggestion into separate changes', () => {
    expect(changes(hs).map(k => [hs[k].removed.join(' '), hs[k].added.join(' ')])).toEqual([
      ['dear mr smith i', 'Dear Mr Smith, I'], ["you're letter", 'your letter.'],
    ]);
  });

  it('makes only the changes accepted', () => {
    const [first, second] = changes(hs);
    expect(compose(hs, new Set())).toBe(before);
    expect(compose(hs, new Set([second]))).toBe('dear mr smith i am writing to you about your letter.');
    expect(compose(hs, new Set([first, second]))).toBe(after);
  });

  it('keeps a typed line break inside its word', () => {
    const h = hunks('end of point.\nnext line', 'End of point.\nnext line');
    expect(compose(h, new Set(changes(h)))).toBe('End of point.\nnext line');
    expect(compose(h, new Set())).toBe('end of point.\nnext line');
  });
});
