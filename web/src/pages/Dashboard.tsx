import { useEffect, useState } from 'react';
import { api, fmtDate, fmtLen, type Mode, type Row } from '../api';

const VIEWS: Record<Mode, [string, string][]> = {
  meeting: [
    ['WithAuthor', 'With Author'], ['WithImporter', 'Imported'], ['AwaitingAssignee', 'Awaiting Assignee'],
    ['WithAssignee', 'With Assignee'], ['ConversionFailed', 'Conversion Failed'], ['Completed', 'Completed'], ['Purged', 'Deleted'],
  ],
  // A dictation is always the author's own, even when imported from a dictaphone, so there is no Imported view.
  dictate: [
    ['Draft', 'Unfinished'], ['WithAuthor', 'With Author'], ['AwaitingAssignee', 'Awaiting Assignee'], ['WithAssignee', 'With Assignee'],
    ['ConversionFailed', 'Conversion Failed'], ['Completed', 'Completed'], ['Purged', 'Deleted'],
  ],
  // The owner's agreed list (2026-10-13): Dictate's views, "Speech to Text" in place of With Author, plus Outstanding Tasks.
  speech: [
    ['Draft', 'Draft'], ['WithAuthor', 'Speech to Text'], ['AwaitingAssignee', 'Awaiting Assignee'], ['WithAssignee', 'With Assignee'],
    ['ConversionFailed', 'Conversion Failed'], ['Completed', 'Completed'],
  ],
};
const DEFAULT_ON = ['Draft', 'WithAuthor', 'WithImporter', 'AwaitingAssignee', 'WithAssignee', 'ConversionFailed'];

// The views someone picked are remembered on this computer, per mode. Storage may be unavailable; then the defaults apply.
type Saved = { on: string[]; selfOnly: boolean; outstanding: boolean };
const KEY = (mode: Mode) => `verbaflow.views.${mode}`;
function remembered(mode: Mode): Saved {
  try {
    const v = JSON.parse(localStorage.getItem(KEY(mode)) ?? 'null');
    if (v && Array.isArray(v.on)) return { on: v.on, selfOnly: !!v.selfOnly, outstanding: !!v.outstanding };
  } catch { /* use the defaults */ }
  return { on: DEFAULT_ON, selfOnly: false, outstanding: false };
}
function remember(mode: Mode, v: Saved) { try { localStorage.setItem(KEY(mode), JSON.stringify(v)); } catch { /* not kept */ } }

/** Where "Carry on" goes for an unfinished item. */
export const carryOnLink = (mode: Mode, id: string) => mode === 'speech' ? `#/speech/live/${id}` : `#/dictate/record/${id}`;
// Only these show in the list. Others (run again, delete) need choices or a reason, so they live on the item page.
const ACTION_LABEL: Record<string, string> = { continue: 'Carry on', open: 'Open', retry: 'Retry', approve: 'Approve', assign: 'Assign', reassign: 'Reassign', accept: 'Accept', return: 'Return', 'request-reopen': 'Reopen' };

export default function Dashboard({ mode }: { mode: Mode }) {
  const dictate = mode === 'dictate';
  const speech = mode === 'speech';
  const [rows, setRows] = useState<Row[] | null>(null);
  const [on, setOn] = useState(() => new Set(remembered(mode).on));
  const [selfOnly, setSelfOnly] = useState(() => remembered(mode).selfOnly);
  // Outstanding Tasks is not a status: it shows everything that needs this person now, whatever its status.
  const [outstanding, setOutstanding] = useState(() => remembered(mode).outstanding);
  useEffect(() => remember(mode, { on: [...on], selfOnly, outstanding }), [mode, on, selfOnly, outstanding]);
  const [search, setSearch] = useState('');
  const [error, setError] = useState('');

  const load = () => api.items(mode).then(setRows).catch(e => setError(e.message));
  useEffect(() => { load(); const t = setInterval(load, 4000); return () => clearInterval(t); }, []);

  const toggle = (k: string) => setOn(s => { const n = new Set(s); n.has(k) ? n.delete(k) : n.add(k); return n; });
  const shown = (rows ?? []).filter(r => (outstanding ? !!r.outstanding : on.has(r.statusCode)) && (!selfOnly || r.tags.includes('Self-assigned')) &&
    (!search || `${r.name} ${r.clientReference ?? ''} ${r.description ?? ''}`.toLowerCase().includes(search.toLowerCase())));
  const count = (k: string) => (rows ?? []).filter(r => r.statusCode === k).length;

  const act = async (r: Row, a: string) => {
    setError('');
    try {
      if (a === 'continue') { location.hash = carryOnLink(mode, r.id); return; }
      if (a === 'open' || a === 'assign' || a === 'reassign' || a === 'request-reopen') { location.hash = `#/items/${r.id}`; return; }
      await api.post(`/items/${r.id}/${a}`);
      load();
    } catch (e: any) { setError(e.message); }
  };

  return (
    <div className="card">
      <div className="item-head">
        <h2>{speech ? 'Speech' : dictate ? 'Dictate' : 'Meeting'}</h2>
        <div className="actions">
          {speech ? <a className="btn primary" href="#/speech/live">Start speaking</a> : <>
          <a className="btn primary" href={dictate ? '#/dictate/record' : '#/record'}>{dictate ? 'New dictation' : 'Record a meeting'}</a>
          <a className="btn" href={dictate ? '#/dictate/import' : '#/import'}>{dictate ? 'Import a dictation' : 'Import a recording'}</a></>}
        </div>
      </div>
      <div className="toggles" role="group" aria-label="Views">
        {VIEWS[mode].map(([k, label]) => (
          <button key={k} className={on.has(k) && !outstanding ? 'on' : ''} aria-pressed={on.has(k) && !outstanding} onClick={() => { setOutstanding(false); toggle(k); }}>{label} ({count(k)})</button>
        ))}
        {speech && <button className={outstanding ? 'on' : ''} aria-pressed={outstanding} title="Everything that needs you now, whatever its status (not Completed)"
          onClick={() => setOutstanding(!outstanding)}>Outstanding Tasks ({(rows ?? []).filter(r => r.outstanding).length})</button>}
        <button className={selfOnly ? 'on' : ''} aria-pressed={selfOnly} onClick={() => setSelfOnly(!selfOnly)}>Self-assigned only</button>
        <input placeholder="Search name, reference, description" value={search} onChange={e => setSearch(e.target.value)} />
      </div>
      {error && <div className="error">{error}</div>}
      <div className="scroll">
        <table>
          <thead><tr><th>Priority</th><th>Status</th><th>Length</th><th>Client Reference</th><th>Description</th><th>Author</th><th>Assigned to</th><th>Created on</th><th>Due date</th><th>Actions</th></tr></thead>
          <tbody>
            {rows === null && <tr><td colSpan={10}>Loading…</td></tr>}
            {rows !== null && shown.length === 0 && <tr><td colSpan={10} className="note">{speech ? 'Nothing to show with these views. Choose Start speaking to get started.' : dictate ? 'Nothing to show with these views. Start a new dictation or import one to get started.' : 'Nothing to show with these views. Record or import a meeting to get started.'}</td></tr>}
            {shown.map(r => (
              <tr key={r.id}>
                <td>{r.priority}</td>
                <td><span className="pill" title={r.statusCode === 'ConversionFailed' && r.failureReason ? r.failureReason : undefined}>{r.status}</span> {r.tags.map(t => <span key={t} className={'tag' + (t === 'Objection logged' ? ' warn' : '')}>{t}</span>)}</td>
                <td>{fmtLen(r.lengthMs)}</td>
                <td>{r.clientReference ?? ''}</td>
                <td className="wrap"><a href={r.actions.includes('continue') ? carryOnLink(mode, r.id) : `#/items/${r.id}`}>{r.name}</a>{r.description ? ` — ${r.description}` : ''}</td>
                <td>{r.author ?? ''}</td>
                <td>{r.assignedTo ?? ''}</td>
                <td>{fmtDate(r.createdOn)}</td>
                <td className={r.overdue ? 'overdue' : ''}>{r.dueDate ? fmtDate(r.dueDate) : ''}{r.overdue && <> <span aria-hidden="true">⚠</span> Overdue</>}</td>
                <td><div className="actions">{r.actions.filter(a => a in ACTION_LABEL).map(a => <button key={a} onClick={() => act(r, a)}>{ACTION_LABEL[a]}</button>)}</div></td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
    </div>
  );
}
