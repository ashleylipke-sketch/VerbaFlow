import { describe, expect, it } from 'vitest';
import { dueWhen } from './DueDate';

describe('dueWhen', () => {
  it('says how far off a due date is and how urgent it looks', () => {
    expect(dueWhen('2026-11-20', '2026-11-05')).toEqual({ words: 'in 15 days', level: 'later' });
    expect(dueWhen('2026-11-08', '2026-11-05')).toEqual({ words: 'in 3 days', level: 'soon' });
    expect(dueWhen('2026-11-06', '2026-11-05')).toEqual({ words: 'tomorrow', level: 'soon' });
    expect(dueWhen('2026-11-05', '2026-11-05')).toEqual({ words: 'today', level: 'soon' });
    expect(dueWhen('2026-11-04', '2026-11-05')).toEqual({ words: '1 day overdue', level: 'late' });
    expect(dueWhen('2026-11-01', '2026-11-05')).toEqual({ words: '4 days overdue', level: 'late' });
    expect(dueWhen('2026-11-01', '2026-11-05', true).level).toBe('done');
  });
});
