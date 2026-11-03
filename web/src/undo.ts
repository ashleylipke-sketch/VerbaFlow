/** Plain words for a change (what Undo would take back, or what an Undo took back): times written as minutes and seconds ("at 1:05", not "at 65000 ms"). */
export function undoLabel(note: string): string {
  return note.replace(/(\d+) ms\b/g, (_, ms: string) => {
    const s = Math.floor(Number(ms) / 1000);
    return `${Math.floor(s / 60)}:${String(s % 60).padStart(2, '0')}`;
  });
}

/** True when the key press is in a place that has its own undo (a text box), so the page's Undo must leave it alone. */
export function typingTarget(el: EventTarget | null): boolean {
  const e = el as HTMLElement | null;
  if (!e || !e.tagName) return false;
  return e.tagName === 'INPUT' || e.tagName === 'TEXTAREA' || e.tagName === 'SELECT' || e.isContentEditable;
}
