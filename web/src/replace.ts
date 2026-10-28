import { diffWords } from './diff';

export type Swap = { from: string; to: string };

const EDGE = /^[\s.,;:!?"'“”‘’()[\]]+|[\s.,;:!?"'“”‘’()[\]]+$/g;
const bare = (s: string) => s.replace(EDGE, '');

/**
 * The word swaps a person made in one box ("Rhys" became "Reiss", "all notes" became "AllNote"), ignoring punctuation
 * around the words and changes that only add or remove words. Up to 8 words on each side.
 */
export function swapsIn(before: string, after: string): Swap[] {
  const ops = diffWords(before, after);
  const out: Swap[] = [];
  let del: string[] = [], ins: string[] = [];
  const flush = () => {
    const from = bare(del.join(' ')), to = bare(ins.join(' '));
    if (from && to && from !== to && del.length <= 8 && ins.length <= 8 && !out.some(s => s.from === from && s.to === to)) out.push({ from, to });
    del = []; ins = [];
  };
  for (const o of ops) {
    if (o.kind === 'del') { if (ins.length) flush(); del.push(o.text); }
    else if (o.kind === 'ins') ins.push(o.text);
    else flush();
  }
  flush();
  return out;
}

const esc = (s: string) => s.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');

/** How many times the words appear as whole words (any capitals, any spacing) in the texts. Same rule as the server. */
export function countIn(texts: string[], words: string): number {
  const parts = words.split(/\s+/).filter(Boolean);
  if (!parts.length) return 0;
  const re = new RegExp(`(?<![\\p{L}\\p{N}])${parts.map(esc).join('\\s+')}(?![\\p{L}\\p{N}])`, 'giu');
  return texts.reduce((n, t) => n + (t.match(re)?.length ?? 0), 0);
}
