import { useEffect, useState } from 'react';
import { api } from '../api';

type Member = { id: string; name: string; how: string };
type Person = { id: string; name: string };
type Dept = { id: string; name: string; groupId: string | null; members: Member[]; mine: boolean; admins: Person[]; iAdminister: boolean };
type Unlinked = { groupId: string; people: number; names: string[] };
type Overview = {
  departments: Dept[]; mine: string[]; canChange: boolean; groupsNotLinked: Unlinked[];
  addableByHand: Person[]; peopleWithTooManyGroups: number; anyGroupsSeen: boolean; people: Person[]; iAdminister: string[];
};

/** Departments: people in the same department can open each other's recordings (not change them). Members come from Microsoft groups. */
export default function DepartmentsPage() {
  const [data, setData] = useState<Overview | null>(null);
  const [editing, setEditing] = useState<string | null>(null); // 'new' or a department's id
  const [name, setName] = useState('');
  const [group, setGroup] = useState('');
  const [adding, setAdding] = useState<Record<string, string>>({});
  const [leading, setLeading] = useState<Record<string, string>>({});
  const [confirming, setConfirming] = useState<string | null>(null);
  const [message, setMessage] = useState('');
  const [error, setError] = useState('');

  const load = () => api.get<Overview>('/departments').then(setData).catch(e => setError(e.message));
  useEffect(() => { load(); }, []);
  const run = async (fn: () => Promise<unknown>, done?: string) => {
    setError(''); setMessage('');
    try { await fn(); await load(); if (done) setMessage(done); } catch (e: any) { setError(e.message); }
  };
  const open = (d: Dept | null, groupId = '') => { setEditing(d?.id ?? 'new'); setName(d?.name ?? ''); setGroup(d?.groupId ?? groupId); setError(''); setMessage(''); };
  const close = () => { setEditing(null); setName(''); setGroup(''); };
  const save = () => run(async () => {
    const body = { name, groupId: group.trim() || null };
    if (editing === 'new') await api.post('/departments', body); else await api.put(`/departments/${editing}`, body);
    close();
  }, editing === 'new' ? `Added ${name.trim()}.` : `Saved ${name.trim()}.`);

  const admin = data?.canChange === true;
  const form = (
    <div className="field" style={{ margin: '12px 0' }}>
      <label className="field">Name
        <input value={name} maxLength={80} onChange={e => setName(e.target.value)} placeholder="e.g. Legal" /></label>
      <label className="field">Microsoft group's Object ID (optional)
        <input value={group} onChange={e => setGroup(e.target.value)} placeholder="e.g. 1a2b3c4d-1234-5678-9abc-1a2b3c4d5e6f" /></label>
      <p className="note">Everyone in this Microsoft group is in the department from their next sign-in. Find the Object ID in Microsoft Entra
        under Groups, on the group's Overview page.</p>
      <div className="actions">
        <button className="primary" disabled={!name.trim()} onClick={save}>{editing === 'new' ? 'Add department' : 'Save changes'}</button>
        <button onClick={close}>Cancel</button>
      </div>
    </div>
  );

  return (
    <div className="card">
      <h2>Departments</h2>
      <p className="note">People in the same department can open each other's recordings: read the text, listen, and export once approved.
        They cannot change, assign, approve or delete them, and unfinished dictations stay private. Who is in a department comes from Microsoft groups.</p>
      <p className="note">A <strong>department admin</strong> can also assign, re-assign and approve the department's recordings, as an administrator can,
        but only those. Reopening and deleting stay with administrators. A department admin does not have to be in the department.</p>
      {error && <div className="error">{error}</div>}
      {message && <div className="ok" role="status">{message}</div>}
      {data && <p><strong>Your departments:</strong> {data.mine.length ? data.mine.join(', ') : 'none'}
        {data.iAdminister.length > 0 && <><br /><strong>You are department admin of:</strong> {data.iAdminister.join(', ')}</>}</p>}
      {admin && editing === null && <div className="actions" style={{ margin: '12px 0' }}><button className="primary" onClick={() => open(null)}>Add a department</button></div>}
      {admin && editing === 'new' && form}
      {data && !admin && <p className="note">Only administrators can change departments.</p>}

      {data === null && !error && <p>Loading…</p>}
      {data && data.departments.length === 0 && <p className="note">There are no departments yet.{admin ? ' Add the first one above.' : ''}</p>}
      {data?.departments.map(d => editing === d.id ? <div key={d.id} className="dept">{form}</div> : (
        <section key={d.id} className="dept" aria-label={d.name}>
          <div className="item-head">
            <h3>{d.name}{d.mine && <span className="tag">You</span>}{d.iAdminister && <span className="tag">You are department admin</span>}</h3>
            {admin && (confirming === d.id
              ? <div className="actions"><button className="danger" onClick={() => run(async () => { await api.del(`/departments/${d.id}`); setConfirming(null); }, `Removed ${d.name}.`)}>Remove</button>
                <button onClick={() => setConfirming(null)}>Keep</button></div>
              : <div className="actions"><button onClick={() => open(d)}>Change</button><button onClick={() => setConfirming(d.id)}>Remove…</button></div>)}
          </div>
          {admin && <p className="note">{d.groupId ? <>Microsoft group <code>{d.groupId}</code></> : 'Not linked to a Microsoft group.'}</p>}
          <p><strong>Members:</strong> {d.members.length === 0 ? <span className="note">Nobody yet.{admin && d.groupId ? ' People appear here once they sign in.' : ''}</span>
            : d.members.map((m, i) => <span key={m.id}>{i > 0 && ', '}{m.name}{m.how === 'by hand' && <span className="note"> (by hand{admin && <> · <button className="link" onClick={() =>
              run(() => api.post(`/departments/${d.id}/members`, { userId: m.id, member: false }))}>take off</button></>})</span>}</span>)}</p>
          <p><strong>Department admins:</strong> {d.admins.length === 0 ? <span className="note">none</span>
            : d.admins.map((p, i) => <span key={p.id}>{i > 0 && ', '}{p.name}{admin && <span className="note"> (<button className="link"
              aria-label={`Stop ${p.name} being a department admin of ${d.name}`} onClick={() =>
              run(() => api.post(`/departments/${d.id}/admins`, { userId: p.id, admin: false }), `${p.name} is no longer a department admin of ${d.name}.`)}>take off</button>)</span>}</span>)}</p>
          {admin && data.people.some(p => !d.admins.some(a => a.id === p.id)) && (
            <div className="actions">
              <select aria-label={`Make someone a department admin of ${d.name}`} value={leading[d.id] ?? ''} onChange={e => setLeading(a => ({ ...a, [d.id]: e.target.value }))}>
                <option value="">Make someone a department admin…</option>
                {data.people.filter(p => !d.admins.some(a => a.id === p.id)).map(p => <option key={p.id} value={p.id}>{p.name}</option>)}
              </select>
              <button disabled={!leading[d.id]} onClick={() => { const who = data.people.find(p => p.id === leading[d.id])?.name; run(async () => {
                await api.post(`/departments/${d.id}/admins`, { userId: leading[d.id], admin: true }); setLeading(a => ({ ...a, [d.id]: '' }));
              }, `${who} is now a department admin of ${d.name}.`); }}>Make department admin</button>
            </div>)}
          {admin && data.addableByHand.some(p => !d.members.some(m => m.id === p.id)) && (
            <div className="actions">
              <select aria-label={`Add a test user to ${d.name}`} value={adding[d.id] ?? ''} onChange={e => setAdding(a => ({ ...a, [d.id]: e.target.value }))}>
                <option value="">Add a test user (no Microsoft sign-in)…</option>
                {data.addableByHand.filter(p => !d.members.some(m => m.id === p.id)).map(p => <option key={p.id} value={p.id}>{p.name}</option>)}
              </select>
              <button disabled={!adding[d.id]} onClick={() => run(async () => {
                await api.post(`/departments/${d.id}/members`, { userId: adding[d.id], member: true }); setAdding(a => ({ ...a, [d.id]: '' }));
              })}>Add</button>
            </div>)}
        </section>))}

      {admin && data && <>
        <h3>Microsoft groups seen at sign-in</h3>
        {!data.anyGroupsSeen && <p className="note">No sign-in has listed any Microsoft groups yet. Microsoft only sends them once the app registration
          is set to include groups (see the setup guide, "Departments"), and then from each person's next sign-in.</p>}
        {data.peopleWithTooManyGroups > 0 && <p className="note">{data.peopleWithTooManyGroups} {data.peopleWithTooManyGroups === 1 ? 'person is' : 'people are'} in too
          many Microsoft groups for the sign-in to list them, so they are in no department. Choose "Groups assigned to the application" in the setup guide to fix this.</p>}
        {data.anyGroupsSeen && data.groupsNotLinked.length === 0 && <p className="note">Every group seen is linked to a department.</p>}
        {data.groupsNotLinked.length > 0 && <div className="scroll"><table>
          <thead><tr><th>Group Object ID</th><th>People in it who have signed in</th><th></th></tr></thead>
          <tbody>{data.groupsNotLinked.map(g => <tr key={g.groupId}>
            <td><code>{g.groupId}</code></td>
            <td className="wrap">{g.people}: {g.names.join(', ')}{g.people > g.names.length ? '…' : ''}</td>
            <td><button onClick={() => open(null, g.groupId)}>Make a department</button></td>
          </tr>)}</tbody>
        </table></div>}
      </>}
    </div>
  );
}
