import { describe, expect, it } from 'vitest';
import { layout, textWords } from './takeMap';

const words = textWords([
  { text: 'Dear John', startMs: 0, endMs: 1000, words: [{ startMs: 0, endMs: 400 }, { startMs: 500, endMs: 1000 }], newParagraph: true },
  { text: 'thank you', startMs: 1000, endMs: 2000, words: null, newParagraph: true },
]);
const view = (s: ReturnType<typeof layout>) => s.map(x => x.kind === 'new' ? `[new ${x.startMs}]` : x.gone ? `-${x.text}` : `${x.text}@${x.startMs}`).join(' ');

describe('text on the recorder', () => {
  it('times words and marks paragraph starts', () => {
    expect(words.map(w => [w.text, w.startMs, w.paraStart])).toEqual([['Dear', 0, true], ['John', 500, false], ['thank', 1000, true], ['you', 1500, false]]);
  });
  it('moves words along after something is put in', () => {
    expect(view(layout(words, [{ at: 1000, mode: 'insert', len: 3000 }], 2000))).toBe('Dear@0 John@500 [new 1000] thank@4000 you@4500');
  });
  it('crosses out words recorded over and adds new recordings at the end', () => {
    expect(view(layout(words, [{ at: 400, mode: 'overwrite', len: 700 }, { at: 0, mode: 'end', len: 500 }], 2000)))
      .toBe('Dear@0 [new 400] -John thank@1000 you@1500 [new 2000]');
  });
  it('takes deleted words out and moves the rest back', () => {
    expect(view(layout(words, [{ at: 400, mode: 'delete', len: 1000 }, { at: 0, mode: 'end', len: 500 }], 2000)))
      .toBe('Dear@0 -John -thank you@500 [new 1000]');
  });
  it('replaces a marked part with a new recording of any length', () => {
    expect(view(layout(words, [{ at: 500, mode: 'replace', len: 2000, cut: 500 }], 2000)))
      .toBe('Dear@0 [new 500] -John thank@2500 you@3000');
  });
});
