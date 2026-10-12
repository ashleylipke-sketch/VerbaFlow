import { useEffect, useMemo, useRef, useState } from 'react';
import { peaks } from './takes';
import { fmtLen } from './api';

export type Selection = { from: number; to: number };

/**
 * The whole dictation as a sound wave, with a cursor that follows playback. Click to put the cursor there; drag across
 * to mark a part (to delete it, or to play just that part). Arrow keys move the cursor half a second; Shift + arrow marks.
 */
export default function WaveEditor({ track, lengthMs, now, playing, cursorMs, selection, onSeek, onSelect }: {
  track: Float32Array; lengthMs: number; now: () => number; playing: boolean; cursorMs: number;
  selection: Selection | null; onSeek: (ms: number) => void; onSelect: (s: Selection | null) => void;
}) {
  const box = useRef<HTMLDivElement>(null);
  const canvas = useRef<HTMLCanvasElement>(null);
  const [width, setWidth] = useState(600);
  const drag = useRef<{ start: number; moved: boolean } | null>(null);
  const [dragSel, setDragSel] = useState<Selection | null>(null);
  const shownSel = dragSel ?? selection;

  useEffect(() => {
    const el = box.current; if (!el) return;
    const ro = new ResizeObserver(() => setWidth(Math.max(100, Math.floor(el.clientWidth))));
    ro.observe(el);
    return () => ro.disconnect();
  }, []);

  const dpr = typeof window !== 'undefined' ? window.devicePixelRatio || 1 : 1;
  const columns = Math.floor(width / 2); // one bar every 2 css pixels
  const bars = useMemo(() => peaks(track, columns), [track, columns]);
  const top = useMemo(() => Math.max(0.05, ...Array.from(bars)), [bars]);

  const draw = (cursor: number) => {
    const c = canvas.current; if (!c) return;
    const h = 96;
    if (c.width !== width * dpr) { c.width = width * dpr; c.height = h * dpr; }
    const g = c.getContext('2d'); if (!g) return;
    const css = getComputedStyle(c);
    const brand = css.getPropertyValue('--brand').trim() || '#2457c5';
    const line = css.getPropertyValue('--muted').trim() || '#888';
    g.setTransform(dpr, 0, 0, dpr, 0, 0);
    g.clearRect(0, 0, width, h);
    const x = (ms: number) => lengthMs ? ms / lengthMs * width : 0;
    if (shownSel) {
      g.fillStyle = 'rgba(214, 69, 65, 0.18)';
      g.fillRect(x(shownSel.from), 0, Math.max(1, x(shownSel.to) - x(shownSel.from)), h);
    }
    const cx = x(cursor);
    for (let i = 0; i < bars.length; i++) {
      const bh = Math.max(1, bars[i] / top * (h / 2 - 4));
      g.fillStyle = i * 2 < cx ? brand : line;
      g.fillRect(i * 2, h / 2 - bh, 1.5, bh * 2);
    }
    g.fillStyle = '#d64541';
    g.fillRect(Math.min(width - 2, Math.max(0, cx - 1)), 0, 2, h);
  };

  // While playing, the cursor follows the audio smoothly; otherwise it sits where it was put.
  useEffect(() => {
    if (!playing) { draw(cursorMs); return; }
    let raf = 0;
    const tick = () => { draw(now()); raf = requestAnimationFrame(tick); };
    tick();
    return () => cancelAnimationFrame(raf);
  });

  const msAt = (clientX: number) => {
    const r = canvas.current!.getBoundingClientRect();
    return Math.round(Math.max(0, Math.min(1, (clientX - r.left) / r.width)) * lengthMs);
  };
  const down = (e: React.PointerEvent) => {
    e.currentTarget.setPointerCapture(e.pointerId);
    drag.current = { start: msAt(e.clientX), moved: false };
  };
  const move = (e: React.PointerEvent) => {
    const d = drag.current; if (!d) return;
    const at = msAt(e.clientX);
    if (Math.abs(at - d.start) > lengthMs * 0.004 + 30) d.moved = true;
    if (d.moved) setDragSel({ from: Math.min(d.start, at), to: Math.max(d.start, at) });
  };
  const up = (e: React.PointerEvent) => {
    const d = drag.current; drag.current = null; if (!d) return;
    const at = msAt(e.clientX);
    setDragSel(null);
    if (d.moved) { const s = { from: Math.min(d.start, at), to: Math.max(d.start, at) }; onSelect(s); onSeek(s.from); }
    else { onSelect(null); onSeek(d.start); }
  };
  const key = (e: React.KeyboardEvent) => {
    const step = e.key === 'ArrowRight' ? 500 : e.key === 'ArrowLeft' ? -500 : e.key === 'End' ? lengthMs : e.key === 'Home' ? -lengthMs : 0;
    if (!step) return;
    e.preventDefault();
    const to = Math.max(0, Math.min(lengthMs, cursorMs + step));
    if (e.shiftKey) {
      const anchor = selection ? (cursorMs === selection.from ? selection.to : selection.from) : cursorMs;
      onSelect(to === anchor ? null : { from: Math.min(anchor, to), to: Math.max(anchor, to) });
    } else onSelect(null);
    onSeek(to);
  };

  return (
    <div ref={box} className="wave-editor">
      <canvas ref={canvas} style={{ width: '100%', height: 96 }} tabIndex={0} role="slider"
        aria-label="Sound wave of the dictation. Click to put the cursor there, drag to mark a part. Arrow keys move the cursor; Shift and arrow keys mark."
        aria-valuemin={0} aria-valuemax={lengthMs} aria-valuenow={cursorMs} aria-valuetext={fmtLen(cursorMs)}
        onPointerDown={down} onPointerMove={move} onPointerUp={up} onKeyDown={key} />
    </div>
  );
}
