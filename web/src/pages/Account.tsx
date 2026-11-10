import { useEffect, useState } from 'react';
import { api } from '../api';

type Account = { name: string; email: string; signedInWith: string; role: string; organisation: string | null; departments: string[]; runs: string[]; individual: boolean };

/** The signed-in person's own account: name, email address, how they signed in, role and departments. */
export default function AccountPage() {
  const [a, setA] = useState<Account | null>(null);
  const [error, setError] = useState('');
  useEffect(() => { api.get<Account>('/me/account').then(setA).catch(e => setError(e.message)); }, []);
  if (error) return <div className="card"><h2>Your account</h2><div className="error">{error}</div></div>;
  if (!a) return <div className="card"><h2>Your account</h2><p className="note">Loading…</p></div>;
  const rows: [string, string][] = [
    ['Name', a.name],
    ['Email address', a.email || 'Not known'],
    ['Signed in with', a.signedInWith],
    ['Role', a.role],
    ...(a.organisation ? [['Organisation', a.organisation] as [string, string]] : []),
    ...(!a.individual ? [['Departments', a.departments.length ? a.departments.join(', ') : 'None'] as [string, string]] : []),
    ...(a.runs.length ? [['Department admin of', a.runs.join(', ')] as [string, string]] : []),
  ];
  return (
    <div className="card" style={{ maxWidth: 640 }}>
      <h2>Your account</h2>
      <table className="account">
        <tbody>{rows.map(([k, v]) => <tr key={k}><th scope="row">{k}</th><td>{v}</td></tr>)}</tbody>
      </table>
      <p className="note">Your name and email address come from the account you signed in with. To change them, change them there and sign in again.
        {a.individual ? <> Your plan and minutes are on <a href="#/plan">Your plan</a>.</> : null}</p>
    </div>
  );
}
