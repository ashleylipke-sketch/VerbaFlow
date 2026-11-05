import { useState } from 'react';
import { api, fmtDate } from './api';

/** Today's date as yyyy-mm-dd on this computer (the smallest date the picker offers). */
export const todayIso = () => { const d = new Date(); d.setMinutes(d.getMinutes() - d.getTimezoneOffset()); return d.toISOString().slice(0, 10); };

/** How far off a due date is, in plain words, and how urgent it looks. `today` is yyyy-mm-dd. */
export function dueWhen(due: string, today: string, done = false): { words: string; level: 'later' | 'soon' | 'late' | 'done' } {
  const days = Math.round((Date.parse(due + 'T00:00:00Z') - Date.parse(today + 'T00:00:00Z')) / 86400000);
  if (done) return { words: '', level: 'done' };
  if (days < 0) return { words: days === -1 ? '1 day overdue' : `${-days} days overdue`, level: 'late' };
  if (days === 0) return { words: 'today', level: 'soon' };
  if (days === 1) return { words: 'tomorrow', level: 'soon' };
  return { words: `in ${days} days`, level: days <= 3 ? 'soon' : 'later' };
}

/** The due date as a coloured badge at the top of the item page, with Set / Change / Remove for those who may assign the recording. */
export default function DueDate({ id, due, done, canSet, onDone }: { id: string; due: string | null; done: boolean; canSet: boolean; onDone: () => void }) {
  const [editing, setEditing] = useState(false);
  const [value, setValue] = useState(due ?? '');
  const [error, setError] = useState('');
  const [busy, setBusy] = useState(false);
  const save = async (to: string | null) => {
    setBusy(true); setError('');
    try { await api.put(`/items/${id}/due`, { dueDate: to }); setEditing(false); onDone(); }
    catch (e: any) { setError(e.message); }
    setBusy(false);
  };
  if (editing) return <div className="due-badge editing">
    <label><strong>Due by</strong> <input type="date" min={todayIso()} value={value} onChange={e => setValue(e.target.value)} autoFocus /></label>
    <button className="primary" disabled={busy || !value || value === due} onClick={() => save(value)}>Save</button>
    {due && <button disabled={busy} onClick={() => save(null)}>Remove</button>}
    <button className="link" onClick={() => { setEditing(false); setValue(due ?? ''); setError(''); }}>Cancel</button>
    {error && <div className="error" style={{ margin: 0, flexBasis: '100%' }}>{error}</div>}
  </div>;
  if (!due) return canSet ? <button className="due-set" onClick={() => { setValue(''); setEditing(true); }}>📅 Set a due date</button> : null;
  const w = dueWhen(due, todayIso(), done);
  return <div className={`due-badge ${w.level}`} role="status">
    <span aria-hidden="true">{w.level === 'late' ? '⚠' : '📅'}</span>
    <span><strong>Due {fmtDate(due)}</strong>{w.words && <> · {w.words}</>}</span>
    {canSet && <button className="link" onClick={() => { setValue(due); setEditing(true); }}>change</button>}
  </div>;
}
