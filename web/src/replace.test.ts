import { describe, expect, it } from 'vitest';
import { countIn, swapsIn } from './replace';

describe('repeated corrections', () => {
  it('finds the words a person swapped, without the punctuation around them', () => {
    expect(swapsIn('Thanks, Rhys. We use all notes.', 'Thanks, Reiss. We use AllNote.')).toEqual([
      { from: 'Rhys', to: 'Reiss' }, { from: 'all notes', to: 'AllNote' }]);
  });
  it('ignores words only added or removed, and punctuation-only changes', () => {
    expect(swapsIn('we agreed', 'we all agreed')).toEqual([]);
    expect(swapsIn('done', 'done.')).toEqual([]);
  });
  it('counts whole words with any capitals and spacing', () => {
    expect(countIn(['Rhys and rhys, not Rhyss', 'All  notes here'], 'Rhys')).toBe(2);
    expect(countIn(['All  notes here', 'all notes'], 'all notes')).toBe(2);
  });
});
