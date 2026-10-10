import { describe, expect, it } from 'vitest';
import { parseSpoken, summaryLanguage } from './languages';

describe('spoken languages', () => {
  it('shows older short codes as full languages', () => expect(parseSpoken('en,fr')).toEqual(['en-GB', 'fr-FR']));
  it('keeps full locales as they are', () => expect(parseSpoken('af-ZA, en-ZA')).toEqual(['af-ZA', 'en-ZA']));
  it('writes the summary in the one spoken language', () => {
    expect(summaryLanguage('af-ZA')).toBe('af');
    expect(summaryLanguage('ja-JP')).toBe('ja');
  });
  it('writes the summary in English when several languages or detection are used', () => {
    expect(summaryLanguage('en-ZA,af-ZA')).toBe('en');
    expect(summaryLanguage('auto')).toBe('en');
  });
});
