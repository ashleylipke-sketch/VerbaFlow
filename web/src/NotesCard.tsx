import { useEffect, useRef, useState } from 'react';
import { api } from './api';

export type NoteRow = { id: string; text: string; by: string; byId: string; at: string; done: boolean; doneBy: string | null; doneAt: string | null; canRemove: boolean };
export type NoteList = { notes: NoteRow[]; canAdd: boolean; why: string | null };

const when = (iso: string) => new Date(iso).toLocaleString('en-GB', { day: 'numeric', month: 'short', hour: '2-digit', minute: '2-digit' });

/** Open notes first (oldest first, so a conversation reads in order), then those dealt with. */
export function sortNotes(notes: NoteRow[]): NoteRow[] {
  return [...notes.filter(n => !n.done), ...notes.filter(n => n.done)];
}

/**
 * Notes for the people working on a recording: "check the client's name", "can I have it by Friday?". Anyone who can
 * open the recording reads them; the people working on it write them and tick them off; the writer can remove theirs.
 * Locked once approved. Opened from a bell notice (…?notes=1), the card is scrolled into view.
 */
export default function NotesCard({ id, onChange }: { id: string; onChange?: () => void }) {
  const [l, setL] = useState<NoteList | null>(null);
  const [text, setText] = useState('');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState('');
  const [showDone, setShowDone] = useState(false);
  const card = useRef<HTMLDivElement>(null);
  useEffect(() => { setL(null); api.get<NoteList>(`/items/${id}/notes`).then(setL).catch(e => setError(e.message)); }, [id]);
  useEffect(() => {
    if (!l || !/[?&]notes=1/.test(location.hash) || !card.current) return;
    card.current.scrollIntoView({ block: 'center' });
    card.current.classList.add('found');
    setTimeout(() => card.current?.classList.remove('found'), 4000);
  }, [l]);

  const run = async (fn: () => Promise<NoteList>, after?: () => void) => {
    setBusy(true); setError('');
    try { setL(await fn()); after?.(); onChange?.(); } catch (e: any) { setError(e.message); } finally { setBusy(false); }
  };
  const add = () => text.trim() && run(() => api.post(`/items/${id}/notes`, { text }), () => setText(''));
  if (!l) return error ? <div className="card"><h2>Notes</h2><div className="error">{error}</div></div> : null;
  if (l.notes.length === 0 && !l.canAdd) return null;

  const open = l.notes.filter(n => !n.done).length;
  const done = l.notes.length - open;
  const shown = sortNotes(l.notes).filter(n => showDone || !n.done);
  return (
    <div className="card notes" ref={card}>
      <div className="att-head">
        <h2>Notes{open > 0 && <span className="note"> ({open} open)</span>}</h2>
        {done > 0 && <button className="link" onClick={() => setShowDone(!showDone)}>{showDone ? 'Hide' : 'Show'} {done} dealt with</button>}
        {l.notes.length === 0 && <span className="note">None yet: leave a note for whoever works on this recording, such as a name to check or when it is needed.</span>}
      </div>
      {shown.map(n => (
        <div key={n.id} className={'note-row' + (n.done ? ' done' : '')}>
          {l.canAdd
            ? <input type="checkbox" checked={n.done} disabled={busy} aria-label={n.done ? 'Mark as not dealt with' : 'Mark as dealt with'}
                title={n.done ? 'Mark as not dealt with' : 'Mark as dealt with'} onChange={e => run(() => api.put(`/items/${id}/notes/${n.id}/done`, { done: e.target.checked }) as any)} />
            : <span className="note-tick" aria-hidden="true">{n.done ? '✓' : '•'}</span>}
          <div className="note-body">
            <div className="note-text">{n.text}</div>
            <div className="note">{n.by} · {when(n.at)}{n.done && n.doneBy ? ` · dealt with by ${n.doneBy}${n.doneAt ? ', ' + when(n.doneAt) : ''}` : ''}
              {n.canRemove && <> · <button className="link" disabled={busy} onClick={() => run(() => api.del(`/items/${id}/notes/${n.id}`) as any)}>Remove</button></>}</div>
          </div>
        </div>))}
      {l.canAdd && <div className="note-add">
        <textarea aria-label="New note" rows={2} maxLength={2000} placeholder="Write a note for the people working on this recording…" value={text}
          onChange={e => setText(e.target.value)} onKeyDown={e => { if (e.key === 'Enter' && (e.ctrlKey || e.metaKey)) { e.preventDefault(); add(); } }} />
        <button className="primary" disabled={busy || !text.trim()} onClick={add}>{busy ? 'Saving…' : 'Add note'}</button>
      </div>}
      {!l.canAdd && l.why && <p className="note">{l.why}</p>}
      {error && <div className="error">{error}</div>}
    </div>
  );
}
