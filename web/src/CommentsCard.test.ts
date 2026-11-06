import { describe, expect, it } from 'vitest';
import { commentMarks, pickWords, sortComments, type CommentRow } from './CommentsCard';

const c = (id: string, resolved: boolean, seg: string | null = 's1'): CommentRow => ({
  id, quote: 'q', text: id, by: 'A', byId: 'a', at: '', atMs: 0, place: seg ? { segmentId: seg, from: 1, to: 3 } : null,
  replies: [], resolved, resolvedBy: null, resolvedAt: null, canRemove: false,
});

describe('comments', () => {
  it('marks only open comments that still have a place, by passage', () => {
    const m = commentMarks({ comments: [c('1', false), c('2', true), c('3', false, null), c('4', false, 's2')], canAdd: true, why: null });
    expect(m.get('s1')).toEqual([{ id: '1', from: 1, to: 3 }]);
    expect(m.get('s2')?.map(x => x.id)).toEqual(['4']);
  });
  it('keeps a selection to the passage where it starts, in word order', () => {
    expect(pickWords([{ seg: 'a', i: 4, text: 'fee.' }, { seg: 'a', i: 3, text: 'the' }, { seg: 'b', i: 0, text: 'Next' }]))
      .toEqual({ segmentId: 'a', from: 3, to: 5, quote: 'the fee.' });
    expect(pickWords([])).toBeNull();
  });
  it('lists open comments first', () => {
    expect(sortComments([c('1', true), c('2', false)]).map(x => x.id)).toEqual(['2', '1']);
  });
});
