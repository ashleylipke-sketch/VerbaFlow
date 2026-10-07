import { useEffect, useState } from 'react';
import { api, type Reopen } from '../api';

export default function ReopenPage() {
  const [list, setList] = useState<Reopen[]>([]);
  const [error, setError] = useState('');
  const load = () => api.reopenRequests().then(setList).catch(e => setError(e.message));
  useEffect(() => { load(); }, []);
  const run = async (fn: () => Promise<unknown>) => { setError(''); try { await fn(); await load(); } catch (e: any) { setError(e.message); } };
  const reject = (r: Reopen) => { const reason = prompt('Why is this being rejected?'); if (reason) run(() => api.post(`/reopen/${r.id}/reject`, { reason })); };

  return (
    <div className="card">
      <h2>Reopen requests</h2>
      <p className="note">Reopening needs two different administrators, and neither can be the person who asked. Approval creates a new linked version; the original stays as it was.</p>
      {error && <div className="error">{error}</div>}
      {list.length === 0 && <p className="note">No requests.</p>}
      {list.map(r => (
        <div key={r.id} className="card">
          <strong><a href={`#/items/${r.itemId}`}>{r.itemName}</a></strong> — requested by {r.requestedBy}
          <div>Reason: {r.reason}</div>
          <div className="note">Approvals: {r.approvals} of 2 {r.approvedBy.length ? `(${r.approvedBy.join(', ')})` : ''}</div>
          {r.open ? <div className="actions" style={{ marginTop: 8 }}>
            <button className="primary" onClick={() => run(() => api.post(`/reopen/${r.id}/approve`))}>Approve</button>
            <button className="danger" onClick={() => reject(r)}>Reject</button></div>
            : <div className="note">{r.outcome}{r.newItemId && <> · <a href={`#/items/${r.newItemId}`}>Open new version</a></>}</div>}
        </div>
      ))}
    </div>
  );
}
