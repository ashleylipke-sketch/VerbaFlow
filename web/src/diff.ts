export type Op = { kind: 'same' | 'ins' | 'del'; text: string; cur?: number };

const tokens = (s: string) => s.split(/\s+/).filter(Boolean);

/** Beyond this many comparisons, show the whole passage as replaced rather than spend the time on a fine-grained diff. */
const MAX_CELLS = 250_000;

/**
 * Word-by-word comparison of the original passage with the current one.
 * `cur` is the index of the word in the current text, so a caller can attach audio timings to it.
 * Within a changed stretch, removed words always come before the words that replaced them.
 */
export function diffWords(original: string, current: string): Op[] {
  const a = tokens(original), b = tokens(current);
  if (a.length * b.length > MAX_CELLS) return group([...a.map((text): Op => ({ kind: 'del', text })), ...b.map((text, cur): Op => ({ kind: 'ins', text, cur }))]);

  // Longest common subsequence table, filled from the end so the walk below runs forwards.
  const lcs: number[][] = Array.from({ length: a.length + 1 }, () => new Array(b.length + 1).fill(0));
  for (let i = a.length - 1; i >= 0; i--)
    for (let j = b.length - 1; j >= 0; j--)
      lcs[i][j] = a[i] === b[j] ? lcs[i + 1][j + 1] + 1 : Math.max(lcs[i + 1][j], lcs[i][j + 1]);

  const ops: Op[] = [];
  let i = 0, j = 0;
  while (i < a.length || j < b.length) {
    if (i < a.length && j < b.length && a[i] === b[j]) { ops.push({ kind: 'same', text: b[j], cur: j }); i++; j++; }
    else if (j < b.length && (i === a.length || lcs[i][j + 1] >= lcs[i + 1][j])) { ops.push({ kind: 'ins', text: b[j], cur: j }); j++; }
    else { ops.push({ kind: 'del', text: a[i] }); i++; }
  }
  return group(ops);
}

/** Puts every run of changes into the order: removed words, then added words. */
function group(ops: Op[]): Op[] {
  const out: Op[] = [];
  let dels: Op[] = [], inss: Op[] = [];
  const flush = () => { out.push(...dels, ...inss); dels = []; inss = []; };
  for (const op of ops) {
    if (op.kind === 'del') dels.push(op);
    else if (op.kind === 'ins') inss.push(op);
    else { flush(); out.push(op); }
  }
  flush();
  return out;
}
