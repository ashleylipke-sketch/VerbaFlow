import { useEffect, useState } from 'react';
import { api, type OrganisationRow } from '../api';

/** VerbaFlow support: the organisations using VerbaFlow, each with its own people, recordings, settings and plan. */
export default function Organisations() {
  const [rows, setRows] = useState<OrganisationRow[] | null>(null);
  const [error, setError] = useState('');
  const [name, setName] = useState('');
  const [tenant, setTenant] = useState('');
  const [editing, setEditing] = useState<string | null>(null);
  const [editName, setEditName] = useState('');
  const [editTenant, setEditTenant] = useState('');
  const [busy, setBusy] = useState(false);
  const [saved, setSaved] = useState('');

  const load = () => api.organisations().then(setRows).catch(e => setError(e.message));
  useEffect(() => { load(); }, []);

  const add = async () => {
    setBusy(true); setError(''); setSaved('');
    try { await api.addOrganisation(name.trim(), tenant.trim()); setSaved(`${name.trim()} added.`); setName(''); setTenant(''); await load(); }
    catch (e: any) { setError(e.message); }
    setBusy(false);
  };
  const save = async (id: string) => {
    setBusy(true); setError(''); setSaved('');
    try { await api.changeOrganisation(id, editName.trim(), editTenant.trim()); setEditing(null); setSaved('Saved.'); await load(); }
    catch (e: any) { setError(e.message); }
    setBusy(false);
  };

  return <>
    <div className="card">
      <h2>Organisations</h2>
      <p className="note">Each organisation has its own people, recordings, administrators, settings (vocabulary, saved text, letterhead,
        minutes templates, retention rules, departments, Widget rules) and plan. Nobody in one organisation sees anything of another.
        People sign in with their organisation’s Microsoft work accounts: link each organisation to its Microsoft directory (its Tenant ID).</p>
      {error && <div className="error" role="alert">{error}</div>}
      {saved && <p className="ok" role="status">{saved}</p>}
      {!rows ? <p className="note">Loading…</p> : <table className="list">
        <thead><tr><th>Name</th><th>Microsoft directory</th><th>People</th><th>Admins</th><th>Plan</th><th></th></tr></thead>
        <tbody>{rows.map(o => editing === o.id
          ? <tr key={o.id}>
              <td data-label="Name"><input aria-label="Name" value={editName} onChange={e => setEditName(e.target.value)} /></td>
              <td data-label="Microsoft directory"><input aria-label="Microsoft directory (Tenant ID)" value={editTenant} onChange={e => setEditTenant(e.target.value)} placeholder="Tenant ID" /></td>
              <td data-label="People">{o.people}</td><td data-label="Admins">{o.admins}</td><td data-label="Plan">{o.plan}</td>
              <td><button className="primary" onClick={() => save(o.id)} disabled={busy || !editName.trim()}>Save</button>{' '}
                <button onClick={() => setEditing(null)}>Cancel</button></td>
            </tr>
          : <tr key={o.id}>
              <td data-label="Name">{o.name}{o.first ? ' (first)' : ''}</td>
              <td data-label="Microsoft directory" className="wrap">{o.tenantId ?? <span className="note">not linked</span>}</td>
              <td data-label="People">{o.people}</td><td data-label="Admins">{o.admins}</td>
              <td data-label="Plan"><a href={`#/plan?organisation=${o.id}`}>{o.plan || 'Plan'}</a></td>
              <td>{o.first
                ? <span className="note" title="Set in VerbaFlow’s settings: Organisation:Name and Entra:TenantId">set in settings</span>
                : <button onClick={() => { setEditing(o.id); setEditName(o.name); setEditTenant(o.tenantId ?? ''); setSaved(''); }}>Change</button>}</td>
            </tr>)}</tbody>
      </table>}
    </div>
    <SharedAddresses />
    <div className="card">
      <h2>Add an organisation</h2>
      <div className="field"><label htmlFor="org-name">Name</label>
        <input id="org-name" value={name} onChange={e => setName(e.target.value)} style={{ maxWidth: 360 }} /></div>
      <div className="field"><label htmlFor="org-tenant">Microsoft directory (Tenant ID, optional; Microsoft Entra admin centre → Overview)</label>
        <input id="org-tenant" value={tenant} onChange={e => setTenant(e.target.value)} placeholder="72f988bf-86f1-41af-91ab-2d7cd011db47" style={{ maxWidth: 360 }} /></div>
      <div className="row"><button className="primary" onClick={add} disabled={busy || !name.trim()}>{busy ? 'Adding…' : 'Add organisation'}</button></div>
      <p className="note">It starts on the Trial plan. Its administrators are the people its Microsoft directory gives the VerbaFlow Admin role
        (see docs/organisations.md).</p>
    </div>
  </>;
}

type Shared = { id: string; name: string; email: string; signedInWith: string; otherSignIn: string; recordings: number };

/**
 * Personal accounts whose email address also signs in another way. One address has one way to sign in, so these are refused
 * at sign-in; support can remove them here (their recordings are deleted).
 */
function SharedAddresses() {
  const [rows, setRows] = useState<Shared[] | null>(null);
  const [error, setError] = useState('');
  const [done, setDone] = useState('');
  const [asking, setAsking] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const load = () => api.get<Shared[]>('/accounts/shared-addresses').then(setRows).catch(e => setError(e.message));
  useEffect(() => { load(); }, []);
  const remove = async (a: Shared) => {
    setBusy(true); setError(''); setDone('');
    try {
      const r = await api.post(`/accounts/${a.id}/remove`) as { recordingsDeleted: number };
      setDone(`Removed the ${a.signedInWith.toLowerCase()} for ${a.email}${r.recordingsDeleted ? ` and its ${r.recordingsDeleted} recording${r.recordingsDeleted === 1 ? '' : 's'}` : ''}.`);
      setAsking(null); await load();
    } catch (e: any) { setError(e.message); }
    setBusy(false);
  };
  return (
    <div className="card">
      <h2>Email addresses with two sign-ins</h2>
      <p className="note">One email address can have only one way to sign in. These personal accounts share their address with another sign-in
        (made before that rule, or before the organisation’s own account), so they can no longer sign in. Removing one deletes its recordings.</p>
      {error && <div className="error" role="alert">{error}</div>}
      {done && <p className="ok" role="status">{done}</p>}
      {!rows ? <p className="note">Loading…</p> : rows.length === 0 ? <p className="note">None.</p> : <table className="list">
        <thead><tr><th>Email address</th><th>Personal account</th><th>Also signs in with</th><th>Recordings</th><th></th></tr></thead>
        <tbody>{rows.map(a => <tr key={a.id}>
          <td data-label="Email address" className="wrap">{a.email}</td>
          <td data-label="Personal account" className="wrap">{a.name}, {a.signedInWith}</td>
          <td data-label="Also signs in with" className="wrap">{a.otherSignIn}</td>
          <td data-label="Recordings">{a.recordings}</td>
          <td>{asking === a.id
            ? <>Remove it{a.recordings ? ` and delete its ${a.recordings} recording${a.recordings === 1 ? '' : 's'}` : ''}? This cannot be undone.{' '}
                <button className="danger" disabled={busy} onClick={() => remove(a)}>Remove</button>{' '}
                <button onClick={() => setAsking(null)}>Cancel</button></>
            : <button onClick={() => { setAsking(a.id); setDone(''); }}>Remove…</button>}</td>
        </tr>)}</tbody>
      </table>}
    </div>
  );
}
