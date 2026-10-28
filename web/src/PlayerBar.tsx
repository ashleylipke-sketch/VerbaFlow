import { useEffect, useState, type RefObject } from 'react';

const SPEEDS = [0.5, 0.75, 0.9, 1, 1.25, 1.5];
const KEY = 'verbaflow.playbackSpeed';
const savedSpeed = () => { try { const n = Number(localStorage.getItem(KEY)); return SPEEDS.includes(n) ? n : 1; } catch { return 1; } };
const fmt = (s: number) => { const t = Math.max(0, Math.floor(s)); return `${Math.floor(t / 60)}:${String(t % 60).padStart(2, '0')}`; };

/**
 * Buttons under the pinned player that never hide (a video's own controls fade out while it plays): play or pause,
 * back and forward 5 seconds, and a slower or faster speed, remembered on this computer. Speech keeps its pitch.
 */
export default function PlayerBar({ player, playing }: { player: RefObject<HTMLMediaElement | null>; playing: boolean }) {
  const [speed, setSpeed] = useState(savedSpeed);
  const [at, setAt] = useState(0);
  useEffect(() => {
    const a = player.current;
    if (!a) return;
    const apply = () => { a.playbackRate = speed; };
    const tick = () => setAt(a.currentTime);
    apply();
    a.addEventListener('loadedmetadata', apply);
    a.addEventListener('timeupdate', tick);
    return () => { a.removeEventListener('loadedmetadata', apply); a.removeEventListener('timeupdate', tick); };
  }, [player, speed]);
  const toggle = () => { const a = player.current; if (!a) return; if (a.paused) a.play().catch(() => undefined); else a.pause(); };
  const skip = (s: number) => { const a = player.current; if (a) a.currentTime = Math.max(0, a.currentTime + s); };
  const choose = (n: number) => { setSpeed(n); try { localStorage.setItem(KEY, String(n)); } catch { /* kept for this page only */ } };
  return (
    <div className="player-bar">
      <button className="primary" onClick={toggle} aria-label={playing ? 'Pause' : 'Play'} style={{ minWidth: 88 }}>{playing ? '❚❚ Pause' : '▶ Play'}</button>
      <button onClick={() => skip(-5)} title="Back 5 seconds">« 5 s</button>
      <button onClick={() => skip(5)} title="Forward 5 seconds">5 s »</button>
      <span className="note">{fmt(at)}</span>
      <label className="note">Speed{' '}
        <select value={speed} onChange={e => choose(Number(e.target.value))}>
          {SPEEDS.map(n => <option key={n} value={n}>{n === 1 ? 'Normal' : `${n}×`}</option>)}
        </select></label>
      <span className="note hint">Ctrl + Space plays or pauses, even while editing</span>
    </div>
  );
}
