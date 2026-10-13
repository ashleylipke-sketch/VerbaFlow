/** One stretch of a passage shown in a paragraph: words [from, to) of passage `seg`. */
export type Piece = { seg: number; from: number; to: number };
type SegText = { text: string; newParagraph: boolean | null };

/** For each word of a passage, whether a line break (typed while editing) comes before it. */
export function breaksBefore(text: string): boolean[] {
  const out: boolean[] = [];
  let pendingBreak = false;
  for (const part of text.split(/(\s+)/)) {
    if (!part) continue;
    if (/^\s+$/.test(part)) { if (/[\n\r]/.test(part)) pendingBreak = true; continue; }
    out.push(pendingBreak && out.length > 0); pendingBreak = false;
  }
  return out;
}

/**
 * The paragraphs of a dictation. A paragraph starts where a passage starts one (said "new paragraph", or typed a line
 * break at its start) and at a line break typed inside a passage. Older dictations, saved before paragraphs were kept,
 * show each passage on its own.
 */
export function paragraphPieces(segs: SegText[]): Piece[][] {
  const out: Piece[][] = [];
  segs.forEach((s, i) => {
    const breaks = breaksBefore(s.text);
    // An emptied passage starts nothing: it stays with the paragraph before, where its removed words show as tracked changes.
    if (out.length === 0 || (s.newParagraph !== false && breaks.length > 0)) out.push([]);
    let from = 0;
    for (let w = 1; w <= breaks.length; w++) {
      if (w === breaks.length || breaks[w]) {
        out[out.length - 1].push({ seg: i, from, to: w });
        if (w < breaks.length) out.push([]);
        from = w;
      }
    }
    if (breaks.length === 0) out[out.length - 1].push({ seg: i, from: 0, to: 0 });
  });
  return out.filter(p => p.some(x => x.to > x.from));
}

/**
 * What goes in front of each paragraph: "•" for a bullet point, "1.", "2." for numbered points (counted afresh after
 * anything that is not a numbered point), null for an ordinary paragraph. Only a paragraph that starts at a passage
 * can be a list point; a line break typed inside a passage starts an ordinary paragraph.
 */
export function paragraphMarkers(segs: { list?: string | null }[], paras: Piece[][]): (string | null)[] {
  let n = 0;
  return paras.map(p => {
    const list = p[0].from === 0 ? segs[p[0].seg].list ?? null : null;
    n = list === 'number' ? n + 1 : 0;
    return list === 'bullet' ? '•' : list === 'number' ? `${n}.` : null;
  });
}
