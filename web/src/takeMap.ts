import { paragraphPieces, breaksBefore } from './paragraphs';
import type { TakeMode } from './takes';

/** One change made to the recording on the recorder page, in milliseconds of the recording as it was at the time. */
export type Take = { at: number; mode: TakeMode; len: number };

/** A word of the converted text, at its time in the recording the text was made from. */
export type TextWord = { text: string; startMs: number; endMs: number; paraStart: boolean };

/** What the page shows: the words where they now are, and the new recordings (not converted yet) between them. */
export type Shown =
  | { kind: 'word'; text: string; startMs: number; endMs: number; paraStart: boolean; gone: boolean }
  | { kind: 'new'; startMs: number; endMs: number; take: number };

type Seg = { text: string; startMs: number; endMs: number; words: { startMs: number; endMs: number }[] | null; newParagraph: boolean | null };

/** The words of a dictation's text with their times; passages without matching word times are spread evenly. */
export function textWords(segs: Seg[]): TextWord[] {
  const starts = new Set<string>();
  for (const p of paragraphPieces(segs)) starts.add(`${p[0].seg}:${p[0].from}`);
  const out: TextWord[] = [];
  segs.forEach((s, i) => {
    const tokens = s.text.split(/\s+/).filter(Boolean);
    const n = tokens.length;
    if (n !== breaksBefore(s.text).length) return;
    const timed = s.words && s.words.length === n ? s.words : null;
    const step = (s.endMs - s.startMs) / Math.max(1, n);
    tokens.forEach((text, w) => out.push({
      text,
      startMs: timed ? timed[w].startMs : Math.round(s.startMs + step * w),
      endMs: timed ? timed[w].endMs : Math.round(s.startMs + step * (w + 1)),
      paraStart: starts.has(`${i}:${w}`),
    }));
  });
  return out;
}

/** Where a point of the recording is after one more take; null when it was recorded over. */
function after(ms: number, t: Take): number | null {
  if (t.mode === 'insert') return ms >= t.at ? ms + t.len : ms;
  if (t.mode === 'overwrite') return ms >= t.at && ms < t.at + t.len ? null : ms;
  return ms;
}

/** The text and the new recordings, in the order they are now heard. */
export function layout(words: TextWord[], takes: Take[], originalMs: number): Shown[] {
  const items: Shown[] = words.map(w => ({ kind: 'word', ...w, gone: false }));
  let length = originalMs;
  takes.forEach((t, k) => {
    for (const x of items) {
      const mid = x.kind === 'word' ? (x.startMs + x.endMs) / 2 : x.startMs;
      const moved = after(mid, t);
      if (moved === null) { if (x.kind === 'word') x.gone = true; else x.endMs = x.startMs; continue; }
      x.startMs += moved - mid; x.endMs += moved - mid;
    }
    const at = t.mode === 'end' ? length : t.at;
    items.push({ kind: 'new', startMs: at, endMs: at + t.len, take: k + 1 });
    length = t.mode === 'insert' || t.mode === 'end' ? length + t.len : Math.max(length, t.at + t.len);
  });
  return items
    .filter(x => x.kind === 'word' || x.endMs > x.startMs)
    .map((x, i) => ({ x, i }))
    // A new recording put in at a word's start comes before that word.
    .sort((a, b) => a.x.startMs - b.x.startMs || (a.x.kind === 'new' ? -1 : 0) - (b.x.kind === 'new' ? -1 : 0) || a.i - b.i)
    .map(({ x }) => x);
}
