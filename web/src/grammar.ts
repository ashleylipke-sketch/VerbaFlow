/** One stretch of a grammar suggestion: words left as they are, or one change (words taken out, words put in). */
export type Hunk = { same: boolean; removed: string[]; added: string[] };

/** Words split on spaces only, so a typed line break stays inside its word and survives an accepted change. */
const words = (s: string) => s.split(' ').filter(Boolean);

/** The passage and the suggestion compared word by word, grouped into unchanged stretches and separate changes. */
export function hunks(before: string, after: string): Hunk[] {
  const a = words(before), b = words(after);
  const l: number[][] = Array.from({ length: a.length + 1 }, () => new Array(b.length + 1).fill(0));
  for (let i = a.length - 1; i >= 0; i--)
    for (let j = b.length - 1; j >= 0; j--)
      l[i][j] = a[i] === b[j] ? l[i + 1][j + 1] + 1 : Math.max(l[i + 1][j], l[i][j + 1]);
  const out: Hunk[] = [];
  const last = () => out[out.length - 1];
  const push = (same: boolean, removed: string[], added: string[]) => {
    const h = last();
    if (h && h.same === same) { h.removed.push(...removed); h.added.push(...added); }
    else out.push({ same, removed: [...removed], added: [...added] });
  };
  let i = 0, j = 0;
  while (i < a.length || j < b.length) {
    if (i < a.length && j < b.length && a[i] === b[j]) { push(true, [a[i]], [a[i]]); i++; j++; }
    else if (j < b.length && (i === a.length || l[i][j + 1] >= l[i + 1][j])) push(false, [], [b[j++]]);
    else push(false, [a[i++]], []);
  }
  return out;
}

/** The passage with only the accepted changes made (by their position in `hs`). */
export function compose(hs: Hunk[], accepted: Set<number>): string {
  return hs.flatMap((h, k) => h.same ? h.removed : accepted.has(k) ? h.added : h.removed).join(' ');
}

/** Positions of the changes (not the unchanged stretches). */
export const changes = (hs: Hunk[]) => hs.flatMap((h, k) => h.same ? [] : [k]);
