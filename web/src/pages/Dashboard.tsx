import { useEffect, useState } from 'react';
import { api, fmtDate, fmtLen, type Row } from '../api';

const VIEWS: [string, string][] = [
  ['WithAuthor', 'With Author'], ['WithImporter', 'Imported'], ['AwaitingAssignee', 'Awaiting Assignee'],
  ['WithAssignee', 'With Assignee'], ['ConversionFailed', 'Conversion Failed'], ['Completed', 'Completed'], ['Purged', 'Deleted'],
];
const DEFAULT_ON = new Set(['WithAuthor', 'WithImporter', 'AwaitingAssignee', 'WithAssignee', 'ConversionFailed']);
// Only these show in the list. Others (run again, delete) need choices or a reason, so they live on the item page.
const ACTION_LABEL: Record<string, string> = { open: 'Open', retry: 'Retry', approve: 'Approve', assign: 'Assign', reassign: 'Reassign', accept: 'Accept', return: 'Return', 'request-reopen': 'Reopen' };

export default function Dashboard() {
  const [rows, setRows] = useState<Row[] | null>(null);
  const [on, setOn] = useState(new Set(DEFAULT_ON));
  const [selfOnly, setSelfOnly] = useState(false);
  const [search, setSearch] = useState('');
  const [error, setError] = useState('');

  const load = () => api.items().then(setRows).catch(e => setError(e.message));
  useEffect(() => { load(); const t = setInterval(load, 4000); return () => clearInterval(t); }, []);

  const toggle = (k: string) => setOn(s => { const n = new Set(s); n.has(k) ? n.delete(k) : n.add(k); return n; });
  const shown = (rows ?? []).filter(r => on.has(r.statusCode) && (!selfOnly || r.tags.includes('Self-assigned')) &&
    (!search || `${r.name} ${r.clientReference ?? ''} ${r.description ?? ''}`.toLowerCase().includes(search.toLowerCase())));
  const count = (k: string) => (rows ?? []).filter(r => r.statusCode === k).length;

  const act = async (r: Row, a: string) => {
    setError('');
    try {
      if (a === 'open' || a === 'assign' || a === 'reassign' || a === 'request-reopen') { location.hash = `#/items/${r.id}`; return; }
      await api.post(`/items/${r.id}/${a}`);
      load();
    } catch (e: any) { setError(e.message); }
  };

  return (
    <div className="card">
      <h2>Meeting</h2>
      <div className="toggles" role="group" aria-label="Views">
        {VIEWS.map(([k, label]) => (
          <button key={k} className={on.has(k) ? 'on' : ''} aria-pressed={on.has(k)} onClick={() => toggle(k)}>{label} ({count(k)})</button>
        ))}
        <button className={selfOnly ? 'on' : ''} aria-pressed={selfOnly} onClick={() => setSelfOnly(!selfOnly)}>Self-assigned only</button>
        <input placeholder="Search name, reference, description" value={search} onChange={e => setSearch(e.target.value)} />
      </div>
      {error && <div className="error">{error}</div>}
      <div className="scroll">
        <table>
          <thead><tr><th>Priority</th><th>Status</th><th>Length</th><th>Client Reference</th><th>Description</th><th>Author</th><th>Assigned to</th><th>Created on</th><th>Due date</th><th>Actions</th></tr></thead>
          <tbody>
            {rows === null && <tr><td colSpan={10}>Loading…</td></tr>}
            {rows !== null && shown.length === 0 && <tr><td colSpan={10} className="note">Nothing to show with these views. Record or import a meeting to get started.</td></tr>}
            {shown.map(r => (
              <tr key={r.id}>
                <td>{r.priority}</td>
                <td><span className="pill">{r.status}</span> {r.tags.map(t => <span key={t} className={'tag' + (t === 'Objection logged' ? ' warn' : '')}>{t}</span>)}</td>
                <td>{fmtLen(r.lengthMs)}</td>
                <td>{r.clientReference ?? ''}</td>
                <td className="wrap"><a href={`#/items/${r.id}`}>{r.name}</a>{r.description ? ` — ${r.description}` : ''}</td>
                <td>{r.author ?? ''}</td>
                <td>{r.assignedTo ?? ''}</td>
                <td>{fmtDate(r.createdOn)}</td>
                <td className={r.overdue ? 'overdue' : ''}>{r.dueDate ? fmtDate(r.dueDate) : ''}{r.overdue ? ' (overdue)' : ''}</td>
                <td><div className="actions">{r.actions.filter(a => a in ACTION_LABEL).map(a => <button key={a} onClick={() => act(r, a)}>{ACTION_LABEL[a]}</button>)}</div></td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
    </div>
  );
}
