import { describe, expect, it } from 'vitest';
import { sortNotes, type NoteRow } from './NotesCard';

const n = (id: string, done: boolean): NoteRow => ({ id, text: id, by: 'A', byId: 'a', at: '', done, doneBy: null, doneAt: null, canRemove: false });

describe('sortNotes', () => {
  it('puts open notes first, keeping their order', () => {
    expect(sortNotes([n('1', true), n('2', false), n('3', false)]).map(x => x.id)).toEqual(['2', '3', '1']);
  });
});
