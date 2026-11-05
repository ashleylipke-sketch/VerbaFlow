import { describe, expect, it } from 'vitest';
import { assignGroups } from './assignOptions';
import type { UserView } from './api';

const u = (id: string, departments: string[] = [], isSupport = false): UserView =>
  ({ id, name: id, email: '', isAdmin: false, isSupport, departments });

describe('assignGroups', () => {
  it('lists the owner\'s department first, then everyone else', () => {
    const g = assignGroups([u('alice', ['Legal']), u('bob', ['Legal']), u('carol', ['Finance']), u('sam', [], true)], ['alice']);
    expect(g.map(x => [x.label, x.people.map(p => p.id)])).toEqual([['Legal', ['alice', 'bob']], ['Everyone else', ['carol']]]);
  });
  it('is one plain list when the owner has no department', () => {
    const g = assignGroups([u('alice'), u('bob', ['Legal'])], ['alice']);
    expect(g).toEqual([{ label: null, people: [u('alice'), u('bob', ['Legal'])] }]);
  });
  it('lists someone in two of the departments once', () => {
    const g = assignGroups([u('alice', ['Finance', 'Legal']), u('bob', ['Finance', 'Legal']), u('dave', ['Legal'])], ['alice']);
    expect(g.map(x => [x.label, x.people.map(p => p.id)])).toEqual([['Finance', ['alice', 'bob']], ['Legal', ['dave']]]);
  });
});
