import { useState } from 'react';
import { api, fmtDate } from './api';

/** Today's date as yyyy-mm-dd on this computer (the smallest date the picker offers). */
export const todayIso = () => { const d = new Date(); d.setMinutes(d.getMinutes() - d.getTimezoneOffset()); return d.toISOString().slice(0, 10); };

/** The due date on the item page, with Set / Change / Remove for those who may assign the recording. */
export default function DueDate({ id, due, overdue, canSet, onDone }: { id: string; due: string | null; overdue: boolean; canSet: boolean; onDone: () => void }) {
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
  if (editing) return <span className="inline-group due-edit">
    <label>Due <input type="date" min={todayIso()} value={value} onChange={e => setValue(e.target.value)} autoFocus /></label>
    <button className="primary" disabled={busy || !value || value === due} onClick={() => save(value)}>Save</button>
    {due && <button disabled={busy} onClick={() => save(null)}>Remove</button>}
    <button className="link" onClick={() => { setEditing(false); setValue(due ?? ''); setError(''); }}>Cancel</button>
    {error && <span className="error" style={{ margin: 0 }}>{error}</span>}
  </span>;
  if (!due && !canSet) return null;
  return <> · {due ? <span className={overdue ? 'overdue' : ''}>Due {fmtDate(due)}{overdue && <> <span aria-hidden="true">⚠</span> Overdue</>}</span> : null}
    {canSet && <> <button className="link" onClick={() => { setValue(due ?? ''); setEditing(true); }}>{due ? 'change' : 'Set a due date'}</button></>}</>;
}
