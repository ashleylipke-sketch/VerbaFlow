import { useEffect, useState } from 'react';
import { api, type SupportError } from '../api';

/** For VerbaFlow support and developers only. Customers never see this page, and the server refuses them if they try. */
export default function Support() {
  const [rows, setRows] = useState<SupportError[] | null>(null);
  const [ref, setRef] = useState('');
  const [error, setError] = useState('');

  const load = (r?: string) => { setError(''); api.supportErrors(r?.trim() || undefined).then(setRows).catch(e => { setRows([]); setError(e.message); }); };
  useEffect(() => { load(); }, []);

  return (
    <div className="card">
      <h2>Support: error log</h2>
      <p className="note">Technical detail for failures, filed under the reference code the customer was shown. Customers and their administrators cannot see this page, or the items you are looking up.</p>
      <div className="actions" style={{ margin: '12px 0' }}>
        <input value={ref} onChange={e => setRef(e.target.value)} onKeyDown={e => e.key === 'Enter' && load(ref)} placeholder="Reference, e.g. VF-7K2Q9M" style={{ minWidth: 260 }} />
        <button className="primary" onClick={() => load(ref)}>Find</button>
        <button onClick={() => { setRef(''); load(); }}>Show latest</button>
      </div>
      {error && <div className="error">{error}</div>}
      {rows === null && <p className="note">Loading…</p>}
      {rows !== null && rows.length === 0 && !error && <p className="note">Nothing recorded{ref ? ` for ${ref}` : ''}.</p>}
      {rows !== null && rows.length > 0 && <table>
        <thead><tr><th>When</th><th>Reference</th><th>Area</th><th>Kind</th><th>Item</th><th>Detail</th></tr></thead>
        <tbody>{rows.map(r => <tr key={r.id}>
          <td>{new Date(r.at).toLocaleString('en-GB')}</td>
          <td><b>{r.reference}</b></td><td>{r.area}</td><td>{r.kind}</td>
          <td className="note">{r.itemId ?? ''}</td>
          <td className="wrap"><details><summary>{r.detail.split('\n')[0].slice(0, 140)}</summary><pre style={{ whiteSpace: 'pre-wrap', fontSize: 12 }}>{r.detail}</pre></details></td>
        </tr>)}</tbody>
      </table>}
    </div>
  );
}
