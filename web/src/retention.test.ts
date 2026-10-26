import { describe, expect, it } from 'vitest';
import { days } from './pages/Retention';

describe('retention days', () => {
  it('says kept for an empty rule and adds years or months where they are whole', () => {
    expect(days(null)).toBe('Kept');
    expect(days(1)).toBe('1 day');
    expect(days(365)).toBe('365 days (1 year)');
    expect(days(2555)).toBe('2555 days (7 years)');
    expect(days(90)).toBe('90 days (about 3 months)');
    expect(days(45)).toBe('45 days');
  });
});
