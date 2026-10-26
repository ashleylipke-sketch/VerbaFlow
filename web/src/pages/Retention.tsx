import { useEffect, useState } from 'react';
import { api, fmtDate, type Retention, type RetentionRules } from '../api';

type Kind = 'meetings' | 'dictations';
type Form = Record<Kind, Record<keyof RetentionRules, string>>;

const toForm = (r: RetentionRules) => ({
  deleteApprovedAfterDays: r.deleteApprovedAfterDays?.toString() ?? '',
  removeRecordingAfterDays: r.removeRecordingAfterDays?.toString() ?? '',
  deleteUnapprovedAfterDays: r.deleteUnapprovedAfterDays?.toString() ?? '',
});
const fromForm = (f: Record<keyof RetentionRules, string>): RetentionRules => {
  const n = (s: string) => (s.trim() === '' ? null : Number(s));
  return { deleteApprovedAfterDays: n(f.deleteApprovedAfterDays), removeRecordingAfterDays: n(f.removeRecordingAfterDays), deleteUnapprovedAfterDays: n(f.deleteUnapprovedAfterDays) };
};

/** "365 days (about 1 year)" or "Kept" for an empty rule. */
export const days = (d: number | null) => {
  if (d === null) return 'Kept';
  const about = d >= 365 && d % 365 === 0 ? ` (${d / 365} year${d === 365 ? '' : 's'})`
    : d >= 60 && d % 30 === 0 ? ` (about ${d / 30} months)` : '';
  return `${d} day${d === 1 ? '' : 's'}${about}`;
};

const LABELS: [keyof RetentionRules, string, string][] = [
  ['deleteApprovedAfterDays', 'Delete approved ones', 'after approval'],
  ['removeRecordingAfterDays', 'Remove only the sound or video of approved ones (the text stays)', 'after approval'],
  ['deleteUnapprovedAfterDays', 'Delete ones never approved', 'after anything last happened to them'],
];

/** How long recordings are kept: the organisation's retention rules, a change waiting for a second administrator, what is due, and holds. */
export default function RetentionPage() {
  const [r, setR] = useState<Retention | null>(null);
  const [form, setForm] = useState<Form | null>(null);
  const [note, setNote] = useState('');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState('');
  const [message, setMessage] = useState('');

  const show = (x: Retention) => { setR(x); setForm({ meetings: toForm(x.meetings), dictations: toForm(x.dictations) }); };
  useEffect(() => { api.retention().then(show).catch(e => setError(e.message)); }, []);

  const run = async (fn: () => Promise<Retention>, done: string) => {
    setBusy(true); setError(''); setMessage('');
    try { show(await fn()); setMessage(done); setNote(''); } catch (e: any) { setError(e.message); } finally { setBusy(false); }
  };

  if (!r || !form) return <div className="card"><h2>Retention</h2>{error ? <div className="error">{error}</div> : <p className="note">Loading…</p>}</div>;
  const p = r.proposal;
  const grace = r.appliesFrom && new Date(r.appliesFrom) > new Date();
  const changed = JSON.stringify(fromForm(form.meetings)) !== JSON.stringify(r.meetings) || JSON.stringify(fromForm(form.dictations)) !== JSON.stringify(r.dictations);

  const rulesTable = (m: RetentionRules, d: RetentionRules) => (
    <div className="scroll"><table className="list">
      <thead><tr><th></th><th>Meetings</th><th>Dictations (Dictate and Speech)</th></tr></thead>
      <tbody>{LABELS.map(([k, label, after]) => (
        <tr key={k}><td className="wrap">{label}</td>
          <td className="wrap">{m[k] === null ? 'Kept' : `${days(m[k])} ${after}`}</td>
          <td className="wrap">{d[k] === null ? 'Kept' : `${days(d[k])} ${after}`}</td></tr>))}</tbody>
    </table></div>
  );

  const input = (kind: Kind, k: keyof RetentionRules, label: string) => (
    <label className="field">{label}
      <input type="number" min={1} max={36500} placeholder="Keep" value={form[kind][k]} disabled={busy}
        aria-label={`${kind === 'meetings' ? 'Meetings' : 'Dictations'}: ${label}`}
        onChange={e => setForm({ ...form, [kind]: { ...form[kind], [k]: e.target.value } })} style={{ width: 120 }} />
    </label>
  );

  return (
    <>
      <div className="card">
        <h2>Retention</h2>
        <p className="note">How long recordings are kept. When a rule deletes a recording, its sound or video, text and summaries go, as with any delete;
          the line in the list and its History stay. An empty rule means kept for ever.</p>
        {error && <div className="error">{error}</div>}
        {message && <div className="ok" role="status">{message}</div>}
        <h3>The rules now</h3>
        {rulesTable(r.meetings, r.dictations)}
        {r.agreedAt
          ? <p className="note">Agreed by {r.agreedBy.join(' and ')} on {fmtDate(r.agreedAt)}.
              {grace ? ` Nothing is deleted under these rules before ${fmtDate(r.appliesFrom!)}, so a mistake can be put right first.` : ''}</p>
          : <p className="note">No rules have been set, so everything is kept.</p>}
      </div>

      {p && <div className="card">
        <h3>A change waiting for a second administrator</h3>
        <p className="note">Proposed by {p.proposedBy} on {fmtDate(p.at)}.{p.note ? ` Note: ${p.note}` : ''}</p>
        {rulesTable(p.meetings, p.dictations)}
        {r.isAdmin && <div className="actions">
          {p.youProposed
            ? <><span className="note">Another administrator must agree before it counts.</span>
                <button disabled={busy} onClick={() => run(api.dropRetention, 'Change withdrawn. The rules stay as they were.')}>Withdraw</button></>
            : <><button className="primary" disabled={busy} onClick={() => run(api.agreeRetention, 'Agreed. The new rules start deleting in a week.')}>Agree</button>
                <button className="danger" disabled={busy} onClick={() => run(api.dropRetention, 'Change rejected. The rules stay as they were.')}>Reject</button></>}
        </div>}
      </div>}

      {r.isAdmin && !p && <div className="card">
        <h3>Change the rules</h3>
        <p className="note">Type a number of days, or leave a box empty to keep. 365 days is a year. Another administrator must agree before a change counts,
          and nothing is deleted under new rules until a week after that.</p>
        <h4>Meetings</h4>
        <div className="pair" style={{ flexWrap: 'wrap' }}>{LABELS.map(([k, label]) => <span key={k}>{input('meetings', k, label)}</span>)}</div>
        <h4>Dictations (Dictate and Speech)</h4>
        <div className="pair" style={{ flexWrap: 'wrap' }}>{LABELS.map(([k, label]) => <span key={k}>{input('dictations', k, label)}</span>)}</div>
        <label className="field">Why (optional)
          <input value={note} maxLength={500} onChange={e => setNote(e.target.value)} placeholder="For example: agreed by the board on 1 November" /></label>
        <div className="actions">
          <button className="primary" disabled={busy || !changed}
            onClick={() => run(() => api.proposeRetention(fromForm(form.meetings), fromForm(form.dictations), note), 'Change proposed. Another administrator must now agree to it.')}>
            Propose this change</button>
        </div>
      </div>}

      {r.isAdmin && <div className="card">
        <h3>Due in the next 30 days</h3>
        {r.due.length === 0 ? <p className="note">Nothing.</p> :
          <div className="scroll"><table className="list">
            <thead><tr><th>Recording</th><th>Kind</th><th>Status</th><th>What happens</th><th>When</th></tr></thead>
            <tbody>{r.due.map(x => (
              <tr key={x.itemId + x.action}><td><a href={`#/items/${x.itemId}`}>{x.name}</a></td><td>{x.mode === 'Meeting' ? 'Meeting' : 'Dictation'}</td>
                <td>{x.status}</td><td>{x.action === 'delete' ? 'Deleted' : 'Sound or video removed, text kept'}</td><td>{fmtDate(x.dueAt)}</td></tr>))}</tbody>
          </table></div>}
        <p className="note">To keep one, open it and use Put on hold.</p>
        <h3>On hold</h3>
        {r.holds.length === 0 ? <p className="note">None.</p> :
          <div className="scroll"><table className="list">
            <thead><tr><th>Recording</th><th>Why</th><th>By</th><th>Since</th></tr></thead>
            <tbody>{r.holds.map(x => (
              <tr key={x.itemId}><td><a href={`#/items/${x.itemId}`}>{x.name}</a></td><td className="wrap">{x.reason}</td><td>{x.by}</td><td>{fmtDate(x.at)}</td></tr>))}</tbody>
          </table></div>}
      </div>}
    </>
  );
}
