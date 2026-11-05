import type { UserView } from './api';

export type AssignGroup = { label: string | null; people: UserView[] };

/**
 * Groups the people for an Assign to… list: first the people in the recording owner's departments (one group per
 * department, by its name), then everyone else. Someone in two of those departments is listed under the first only.
 * With no departments, one plain group.
 */
export function assignGroups(users: UserView[], ownerIds: (string | null | undefined)[]): AssignGroup[] {
  const people = users.filter(u => !u.isSupport);
  const owners = new Set(ownerIds.filter((x): x is string => !!x));
  const names: string[] = [];
  for (const u of people) if (owners.has(u.id)) for (const d of u.departments ?? []) if (!names.includes(d)) names.push(d);
  names.sort((a, b) => a.localeCompare(b));
  if (names.length === 0) return [{ label: null, people }];
  const used = new Set<string>();
  const groups: AssignGroup[] = [];
  for (const name of names) {
    const inIt = people.filter(u => !used.has(u.id) && (u.departments ?? []).includes(name));
    inIt.forEach(u => used.add(u.id));
    if (inIt.length) groups.push({ label: name, people: inIt });
  }
  const rest = people.filter(u => !used.has(u.id));
  if (rest.length) groups.push({ label: 'Everyone else', people: rest });
  return groups;
}

/** The options of an Assign to… list, grouped as {@link assignGroups} says. */
export function AssignOptions({ users, ownerIds }: { users: UserView[]; ownerIds: (string | null | undefined)[] }) {
  return <>{assignGroups(users, ownerIds).map(g => g.label === null
    ? g.people.map(u => <option key={u.id} value={u.id}>{u.name}</option>)
    : <optgroup key={g.label} label={g.label}>{g.people.map(u => <option key={u.id} value={u.id}>{u.name}</option>)}</optgroup>)}</>;
}
