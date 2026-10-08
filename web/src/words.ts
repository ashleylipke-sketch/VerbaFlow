export type Word = { text: string; startMs: number; endMs: number };
export type SegLike = { text: string; startMs: number; endMs: number; words: Word[] | null };

/** How long after a passage ends it still counts as "being read", so short gaps don't flicker. */
export const LINGER_MS = 700;

/**
 * The words of a passage with a time for each. Uses the speech service's timings when they still match the text.
 * After an edit that added or removed words, or for older transcripts, spreads the words across the passage in
 * proportion to their length.
 */
export function wordsFor(seg: SegLike): Word[] {
  const tokens = seg.text.split(/\s+/).filter(Boolean);
  if (seg.words && seg.words.length === tokens.length) return seg.words.map((w, i) => ({ ...w, text: tokens[i] }));
  const total = tokens.reduce((n, t) => n + t.length + 1, 0) || 1;
  const span = Math.max(0, seg.endMs - seg.startMs);
  let used = 0;
  return tokens.map(t => {
    const start = seg.startMs + Math.round((used / total) * span);
    used += t.length + 1;
    return { text: t, startMs: start, endMs: seg.startMs + Math.round((used / total) * span) };
  });
}

/** Index of the word being spoken at `ms`: the last word that has started. -1 before the first word. */
export function activeWordIndex(words: Word[], ms: number): number {
  let idx = -1;
  for (let i = 0; i < words.length; i++) {
    if (words[i].startMs <= ms) idx = i;
    else break;
  }
  return idx;
}

/** Index of the passage being read at `ms`, or -1 in silence before the first passage or after a long gap. */
export function activeSegmentIndex(segs: SegLike[], ms: number): number {
  let idx = -1;
  for (let i = 0; i < segs.length; i++) {
    if (segs[i].startMs <= ms) idx = i;
    else break;
  }
  if (idx < 0) return -1;
  return ms <= segs[idx].endMs + LINGER_MS ? idx : -1;
}
