import { describe, expect, it } from 'vitest';
import { firstWords } from './pages/Speech';

describe('firstWords', () => {
  it('keeps the text up to the end of the first words, line breaks included', () => {
    expect(firstWords('Dear Sir,\nthank you for', 3)).toBe('Dear Sir,\nthank');
    expect(firstWords('one two', 5)).toBe('one two');
    expect(firstWords('one two', 0)).toBe('');
  });
});
