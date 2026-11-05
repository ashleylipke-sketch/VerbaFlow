import { useEffect, useState } from 'react';
import { api, type Workload, type WorkloadItem } from '../api';

const kinds: [string, string][] = [['all', 'All recordings'], ['meeting', 'Meetings'], ['dictate', 'Dictations'], ['speech', 'Speech']];
const periods: [number, string][] = [[7, 'Last 7 days'], [30, 'Last 30 days'], [90, 'Last 3 months'], [365, 'Last year']];
const modeLabel: Record<string, string> = { Meeting: 'Meeting', Dictate: 'Dictation', Speech: 'Speech' };

/** A time in hours, in a few words. */
export function took(hours: number | null): string {
  if (hours === null) return '–';
  if (hours < 1) return `${Math.max(1, Math.round(hours * 60))} min`;
  if (hours < 48) return `${Math.round(hours)} h`;
  return `${Math.round(hours / 24)} days`;
}

const since = (iso: string) => {
  const days = Math.floor((Date.now() - new Date(iso).getTime()) / 86_400_000);
  return days <= 0 ? 'today' : days === 1 ? '1 day' : `${days} days`;
};

const remembered = <T,>(key: string, fallback: T): T => {
  try { const v = localStorage.getItem(key); return v ? JSON.parse(v) as T : fallback; } catch { return fallback; }
};
const remember = (key: string, value: unknown) => { try { localStorage.setItem(key, JSON.stringify(value)); } catch { /* not kept */ } };

/** Administrators' overview: who has what now, what is overdue, and how long each step takes. */
export default function WorkloadPage() {
  const [mode, setMode] = useState<string>(() => remembered('verbaflow.workload.mode', 'all'));
  const [days, setDays] = useState<number>(() => remembered('verbaflow.workload.days', 30));
  const [data, setData] = useState<Workload | null>(null);
  const [error, setError] = useState('');

  useEffect(() => {
    remember('verbaflow.workload.mode', mode); remember('verbaflow.workload.days', days);
    setError('');
    api.workload(mode, days).then(setData).catch(e => setError(e.message));
  }, [mode, days]);

  const period = periods.find(([d]) => d === days)?.[1].toLowerCase() ?? `last ${days} days`;

  return (
    <div className="card workload">
      <h2>Workload</h2>
      <p className="note">Who has which recordings now, what is overdue, and how long each step has taken. Only administrators see this page.</p>
      <div className="row" style={{ flexWrap: 'wrap', gap: 12 }}>
        <label>Show <select value={mode} onChange={e => setMode(e.target.value)}>{kinds.map(([k, l]) => <option key={k} value={k}>{l}</option>)}</select></label>
        <label>Done in the <select value={days} onChange={e => setDays(Number(e.target.value))}>{periods.map(([d, l]) => <option key={d} value={d}>{l.toLowerCase()}</option>)}</select></label>
      </div>
      {error && <div className="error" role="alert">{error}</div>}
      {!data && !error && <p className="note">Loading…</p>}
      {data && <>
        <div className="tiles">
          <div className="tile"><strong>{data.open}</strong><span>waiting on someone</span></div>
          <div className={`tile${data.overdue ? ' bad' : ''}`}><strong>{data.overdue}</strong><span>overdue</span></div>
          <div className="tile"><strong>{data.dueToday}</strong><span>due today</span></div>
          <div className="tile"><strong>{data.approvedInPeriod}</strong><span>approved, {period}</span></div>
          <div className="tile"><strong>{data.daysToApprove === null ? '–' : took(data.daysToApprove * 24)}</strong><span>usual time from recording to approval</span></div>
        </div>

        <h3>People</h3>
        {data.people.length === 0 ? <p className="note">Nobody has any recordings waiting, and nothing was assigned, returned or approved {period}.</p> :
          <div className="scroll"><table className="list">
            <thead>
              <tr><th>Person</th><th title="Their own recordings waiting for them to check, assign or approve">Own to check</th><th>To accept</th><th>Being typed</th>
                <th>Overdue</th><th>Assigned to them</th><th>Returned</th><th>Approved</th>
                <th title="From being assigned to accepting it (the usual time, half are quicker)">Time to accept</th>
                <th title="From accepting it to returning it (the usual time)">Time typing</th>
                <th title="From the text being ready, or returned, to approving it (the usual time)">Time to approve</th></tr>
            </thead>
            <tbody>
              {data.people.map(p => <tr key={p.userId}>
                <td data-label="Person"><strong>{p.name}</strong>{p.dueToday > 0 && <span className="tag">{p.dueToday} due today</span>}</td>
                <td data-label="Own to check">{p.asAuthor || ''}</td>
                <td data-label="To accept">{p.toAccept || ''}</td>
                <td data-label="Being typed">{p.beingTyped || ''}</td>
                <td data-label="Overdue">{p.overdue ? <span className="overdue">⚠ {p.overdue}</span> : ''}</td>
                <td data-label="Assigned to them">{p.assignedToThem || ''}</td>
                <td data-label="Returned">{p.returned || ''}</td>
                <td data-label="Approved">{p.approved || ''}</td>
                <td data-label="Time to accept">{p.hoursToAccept === null ? '' : took(p.hoursToAccept)}</td>
                <td data-label="Time typing">{p.hoursTyping === null ? '' : took(p.hoursTyping)}</td>
                <td data-label="Time to approve">{p.hoursToApprove === null ? '' : took(p.hoursToApprove)}</td>
              </tr>)}
            </tbody>
          </table></div>}
        <p className="note">The first four columns are what each person has now. The others count what happened {period}; times are the usual time (half were quicker, half slower).</p>

        <h3>Overdue</h3>
        {data.overdueItems.length === 0 ? <p className="note">Nothing is overdue.</p> : <Items rows={data.overdueItems} late />}

        <h3>Waiting longest</h3>
        <p className="note">The ten recordings that have been with the same person longest, counted from when they last reached them (made, converted, assigned, accepted or returned).</p>
        {data.waitingLongest.length === 0 ? <p className="note">Nothing is waiting.</p> : <Items rows={data.waitingLongest} />}
      </>}
    </div>
  );
}

function Items({ rows, late }: { rows: WorkloadItem[]; late?: boolean }) {
  return (
    <div className="scroll"><table className="list">
      <thead><tr><th>Recording</th><th>Kind</th><th>With</th><th>Stage</th><th>Due</th><th>{late ? 'Late by' : 'With them for'}</th></tr></thead>
      <tbody>
        {rows.map(r => <tr key={r.itemId}>
          <td data-label="Recording"><a href={`#/items/${r.itemId}`}><strong>{r.name}</strong></a></td>
          <td data-label="Kind">{modeLabel[r.mode] ?? r.mode}</td>
          <td data-label="With">{r.holder}</td>
          <td data-label="Stage">{r.stage}</td>
          <td data-label="Due">{r.due ? new Date(r.due + 'T00:00').toLocaleDateString(undefined, { day: 'numeric', month: 'short', year: 'numeric' }) : ''}</td>
          <td data-label={late ? 'Late by' : 'With them for'}>{late ? <span className="overdue">{r.daysLate === 1 ? '1 day' : `${r.daysLate} days`}</span> : since(r.since)}</td>
        </tr>)}
      </tbody>
    </table></div>
  );
}
