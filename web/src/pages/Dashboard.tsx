import { AssignOptions } from '../assignOptions';
import { dueWhen, todayIso } from '../DueDate';
import { useEffect, useState } from 'react';
import { api, fmtDate, fmtLen, type BulkResult, type Mode, type Row, type UserView } from '../api';

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
type Saved = { on: string[]; selfOnly: boolean; outstanding: boolean; department: boolean };
const KEY = (mode: Mode) => `verbaflow.views.${mode}`;
function remembered(mode: Mode): Saved {
  try {
    const v = JSON.parse(localStorage.getItem(KEY(mode)) ?? 'null');
    if (v && Array.isArray(v.on)) return { on: v.on, selfOnly: !!v.selfOnly, outstanding: !!v.outstanding, department: v.department !== false };
  } catch { /* use the defaults */ }
  return { on: DEFAULT_ON, selfOnly: false, outstanding: false, department: true };
}
function remember(mode: Mode, v: Saved) { try { localStorage.setItem(KEY(mode), JSON.stringify(v)); } catch { /* not kept */ } }

/** Where "Carry on" goes for an unfinished item. */
export const carryOnLink = (mode: Mode, id: string) => mode === 'speech' ? `#/speech/live/${id}` : `#/dictate/record/${id}`;
// Only these show in the list. Others (run again, delete) need choices or a reason, so they live on the item page.
// What can be done to several ticked recordings at once (each still gets the usual checks).
const BULK = ['assign', 'accept', 'return', 'approve'] as const;
const canBulk = (r: Row, a: string) => a === 'assign' ? r.actions.includes('assign') || r.actions.includes('reassign') : r.actions.includes(a);
const ACTION_LABEL: Record<string, string> = { continue: 'Carry on', open: 'Open', retry: 'Retry', approve: 'Approve', assign: 'Assign', reassign: 'Reassign', accept: 'Accept', return: 'Return', 'request-reopen': 'Reopen' };

export default function Dashboard({ mode }: { mode: Mode }) {
  const dictate = mode === 'dictate';
  const speech = mode === 'speech';
  const [rows, setRows] = useState<Row[] | null>(null);
  const [on, setOn] = useState(() => new Set(remembered(mode).on));
  const [selfOnly, setSelfOnly] = useState(() => remembered(mode).selfOnly);
  // Outstanding Tasks is not a status: it shows everything that needs this person now, whatever its status.
  const [outstanding, setOutstanding] = useState(() => remembered(mode).outstanding);
  // Recordings by people in the same department (they can be opened, not changed). Shown unless switched off.
  const [department, setDepartment] = useState(() => remembered(mode).department);
  useEffect(() => remember(mode, { on: [...on], selfOnly, outstanding, department }), [mode, on, selfOnly, outstanding, department]);
  const [search, setSearch] = useState('');
  const [error, setError] = useState('');

  const [ticked, setTicked] = useState<Set<string>>(new Set());
  const [users, setUsers] = useState<UserView[]>([]);
  const [assignTo, setAssignTo] = useState('');
  const [reason, setReason] = useState('');
  const [assignDue, setAssignDue] = useState('');
  const [busy, setBusy] = useState(false);
  const [results, setResults] = useState<{ what: string; list: BulkResult[] } | null>(null);
  useEffect(() => { api.users().then(u => setUsers(u.filter(x => !x.isSupport))).catch(() => {}); }, []);

  const load = () => api.items(mode).then(setRows).catch(e => setError(e.message));
  useEffect(() => { load(); const t = setInterval(load, 4000); return () => clearInterval(t); }, []);

  const toggle = (k: string) => setOn(s => { const n = new Set(s); n.has(k) ? n.delete(k) : n.add(k); return n; });
  const anyDepartment = (rows ?? []).some(r => r.fromDepartment);
  const shown = (rows ?? []).filter(r => (department || !r.fromDepartment) && (outstanding ? !!r.outstanding : on.has(r.statusCode)) && (!selfOnly || r.tags.includes('Self-assigned')) &&
    (!search || `${r.name} ${r.clientReference ?? ''} ${r.description ?? ''}`.toLowerCase().includes(search.toLowerCase())));
  const tickable = shown.filter(r => BULK.some(a => canBulk(r, a)));
  const chosen = tickable.filter(r => ticked.has(r.id));
  const tick = (id: string) => setTicked(s => { const n = new Set(s); n.has(id) ? n.delete(id) : n.add(id); return n; });
  const allTicked = tickable.length > 0 && chosen.length === tickable.length;
  // A reason is needed only when taking a recording away from someone else who has it (as on the item page).
  const target = assignTo === 'none' ? null : users.find(u => u.id === assignTo)?.name ?? null;
  const needsReason = chosen.some(r => r.actions.includes('reassign') && !!r.assignedTo && r.assignedTo !== target && r.assignedTo !== r.author);
  const bulk = async (a: typeof BULK[number]) => {
    const rowsFor = chosen.filter(r => canBulk(r, a));
    if (a === 'approve' && !confirm(`Approving locks ${rowsFor.length === 1 ? 'this recording' : `these ${rowsFor.length} recordings`}. Continue?`)) return;
    setBusy(true); setError(''); setResults(null);
    try {
      const list = await api.bulk(a, rowsFor.map(r => r.id), a === 'assign' ? (assignTo === 'none' ? null : assignTo) : null, a === 'assign' ? reason : null, a === 'assign' && assignTo !== 'none' && assignDue ? assignDue : null);
      const what = a === 'assign' ? (assignTo === 'none' ? 'Taken back' : `Assigned to ${users.find(u => u.id === assignTo)?.name ?? 'them'}${assignDue ? `, due ${fmtDate(assignDue)}` : ''}`)
        : a === 'accept' ? 'Accepted' : a === 'return' ? 'Returned' : 'Approved';
      setResults({ what, list });
      setTicked(new Set(list.filter(x => !x.done).map(x => x.id)));
      if (a === 'assign') { setAssignTo(''); setReason(''); setAssignDue(''); }
      load();
    } catch (e: any) { setError(e.message); } finally { setBusy(false); }
  };
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
          <a className="btn" href={dictate ? '#/dictate/import' : '#/import'}>{dictate ? 'Import a dictation' : 'Import a recording'}</a>
          {!dictate && <a className="btn" href="#/meet" title="A visible note-taker joins a Teams, Zoom, Google Meet or Webex call and records it">Online meeting</a>}</>}
        </div>
      </div>
      <div className="toggles" role="group" aria-label="Views">
        {VIEWS[mode].map(([k, label]) => (
          <button key={k} className={on.has(k) && !outstanding ? 'on' : ''} aria-pressed={on.has(k) && !outstanding} onClick={() => { setOutstanding(false); toggle(k); }}>{label} ({count(k)})</button>
        ))}
        {speech && <button className={outstanding ? 'on' : ''} aria-pressed={outstanding} title="Everything that needs you now, whatever its status (not Completed)"
          onClick={() => setOutstanding(!outstanding)}>Outstanding Tasks ({(rows ?? []).filter(r => r.outstanding).length})</button>}
        <button className={selfOnly ? 'on' : ''} aria-pressed={selfOnly} onClick={() => setSelfOnly(!selfOnly)}>Self-assigned only</button>
        {anyDepartment && <button className={department ? 'on' : ''} aria-pressed={department} title="Recordings by people in your department. You can open them but not change them."
          onClick={() => setDepartment(!department)}>My department's ({(rows ?? []).filter(r => r.fromDepartment).length})</button>}
        <input placeholder="Search name, reference, description" value={search} onChange={e => setSearch(e.target.value)} />
      </div>
      {error && <div className="error">{error}</div>}
      {chosen.length > 0 && <div className="bulk-bar" role="group" aria-label="Ticked recordings">
        <strong>{chosen.length} ticked</strong>
        {BULK.filter(a => a !== 'assign').map(a => {
          const n = chosen.filter(r => canBulk(r, a)).length;
          return n > 0 && <button key={a} className={a === 'approve' ? 'primary' : ''} disabled={busy} onClick={() => bulk(a)}>
            {a === 'accept' ? 'Accept' : a === 'return' ? 'Return to owner' : 'Approve'}{n < chosen.length ? ` (${n})` : ''}</button>;
        })}
        {chosen.some(r => canBulk(r, 'assign')) && <span className="inline-group">
          <select aria-label="Assign the ticked recordings to" value={assignTo} onChange={e => setAssignTo(e.target.value)}>
            <option value="">Assign to…</option><option value="none">Nobody (take back)</option>
            <AssignOptions users={users} ownerIds={chosen.filter(r => canBulk(r, 'assign')).map(r => r.ownerId)} />
          </select>
          {assignTo && assignTo !== 'none' && <label className="note">Due by <input type="date" aria-label="Due by (optional)" min={todayIso()} value={assignDue} onChange={e => setAssignDue(e.target.value)} /></label>}
          {assignTo && needsReason && <input placeholder="Reason (some are with someone else)" value={reason} onChange={e => setReason(e.target.value)} />}
          {assignTo && <button disabled={busy || (needsReason && !reason.trim())} onClick={() => bulk('assign')}>
            Apply{chosen.filter(r => canBulk(r, 'assign')).length < chosen.length ? ` (${chosen.filter(r => canBulk(r, 'assign')).length})` : ''}</button>}
        </span>}
        <button className="link" disabled={busy} onClick={() => setTicked(new Set())}>Clear ticks</button>
        {busy && <span className="note">Working…</span>}
      </div>}
      {results && <div className="bulk-results" role="status">
        <span className="ok">{results.what}: {results.list.filter(x => x.done).length} of {results.list.length}.</span>
        {results.list.some(x => !x.done) && <> Not done (still ticked):<ul>{results.list.filter(x => !x.done).map(x => <li key={x.id}><b>{x.name}</b>: {x.problem}</li>)}</ul></>}
        {" "}<button className="link" onClick={() => setResults(null)}>Close</button>
      </div>}
      <div className="scroll">
        <table className="list">
          <thead><tr><th className="tick">{tickable.length > 0 && <input type="checkbox" aria-label="Tick all shown" checked={allTicked}
            onChange={() => setTicked(allTicked ? new Set() : new Set(tickable.map(r => r.id)))} />}</th><th>Priority</th><th>Status</th><th>Length</th><th>Client Reference</th><th>Description</th><th>Author</th><th>Assigned to</th><th>Created on</th><th>Due date</th><th>Actions</th></tr></thead>
          <tbody>
            {rows === null && <tr><td colSpan={11}>Loading…</td></tr>}
            {rows !== null && shown.length === 0 && <tr><td colSpan={11} className="note">{speech ? 'Nothing to show with these views. Choose Start speaking to get started.' : dictate ? 'Nothing to show with these views. Start a new dictation or import one to get started.' : 'Nothing to show with these views. Record or import a meeting to get started.'}</td></tr>}
            {shown.map(r => (
              <tr key={r.id} className={ticked.has(r.id) ? 'ticked' : ''}>
                <td data-label="Tick" className="tick">{BULK.some(a => canBulk(r, a)) && <input type="checkbox" aria-label={`Tick ${r.name}`} checked={ticked.has(r.id)} onChange={() => tick(r.id)} />}</td>
                <td data-label="Priority">{r.priority}</td>
                <td data-label="Status"><span className="pill" title={r.statusCode === 'ConversionFailed' && r.failureReason ? r.failureReason : undefined}>{r.status}</span> {r.tags.map(t => <span key={t} className={'tag' + (t === 'Objection logged' ? ' warn' : '')}>{t}</span>)}</td>
                <td data-label="Length">{fmtLen(r.lengthMs)}</td>
                <td data-label="Client Reference">{r.clientReference ?? ''}</td>
                <td data-label="Description" className="wrap"><a href={r.actions.includes('continue') ? carryOnLink(mode, r.id) : `#/items/${r.id}`}>{r.name}</a>{!!r.attachments && <> <span className="clip" title={`${r.attachments} attached file${r.attachments === 1 ? '' : 's'}`}
                  aria-label={`${r.attachments} attached file${r.attachments === 1 ? '' : 's'}`}>📎{r.attachments}</span></>}{r.description ? ` — ${r.description}` : ''}</td>
                <td data-label="Author">{r.author ?? ''}</td>
                <td data-label="Assigned to">{r.assignedTo ?? ''}</td>
                <td data-label="Created on">{fmtDate(r.createdOn)}</td>
                <td data-label="Due date" className={r.overdue ? 'overdue' : r.dueDate && dueWhen(r.dueDate, todayIso(), r.statusCode === 'Completed').level === 'soon' ? 'due-soon' : ''}>{r.dueDate ? fmtDate(r.dueDate) : ''}{r.overdue
  ? <> <span aria-hidden="true">⚠</span> Overdue</>
  : r.dueDate && r.statusCode !== 'Completed' && dueWhen(r.dueDate, todayIso()).level === 'soon' ? <> ({dueWhen(r.dueDate, todayIso()).words})</> : null}</td>
                <td data-label="Actions"><div className="actions">{r.actions.filter(a => a in ACTION_LABEL).map(a => <button key={a} onClick={() => act(r, a)}>{ACTION_LABEL[a]}</button>)}</div></td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
    </div>
  );
}
