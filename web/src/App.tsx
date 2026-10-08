import { useEffect, useState } from 'react';
import { api, getUser, setUser, type UserView } from './api';
import Dashboard from './pages/Dashboard';
import Record from './pages/Record';
import Import from './pages/Import';
import ItemPage from './pages/ItemPage';
import Reopen from './pages/Reopen';
import Vocabulary from './pages/Vocabulary';

function useHash() {
  const [h, setH] = useState(location.hash || '#/');
  useEffect(() => { const f = () => setH(location.hash || '#/'); addEventListener('hashchange', f); return () => removeEventListener('hashchange', f); }, []);
  return h;
}

export default function App() {
  const hash = useHash();
  const [users, setUsers] = useState<UserView[]>([]);
  const [me, setMe] = useState(getUser());
  useEffect(() => { fetch('/api/users', { headers: { 'X-Dev-User': 'alice' } }).then(r => r.json()).then(setUsers); }, []);
  const key = (u: UserView) => u.email.split('@')[0];
  const path = hash.replace(/^#/, '');
  const item = path.match(/^\/items\/([0-9a-f-]{36})$/);
  const admin = users.find(u => key(u) === me)?.isAdmin;

  return (
    <>
      <header className="top">
        <h1>VerbaFlow · Speak</h1>
        <nav>
          <a href="#/" className={path === '/' ? 'on' : ''}>Meeting</a>
          <a href="#/record" className={path === '/record' ? 'on' : ''}>Record</a>
          <a href="#/import" className={path === '/import' ? 'on' : ''}>Import</a>
          <a href="#/vocabulary" className={path === '/vocabulary' ? 'on' : ''}>Vocabulary</a>
          {admin && <a href="#/reopen" className={path === '/reopen' ? 'on' : ''}>Reopen requests</a>}
        </nav>
        <label className="note" title="Development stand-in for Microsoft Entra ID sign-in">
          Signed in as{' '}
          <select value={me} onChange={e => { setUser(e.target.value); setMe(e.target.value); }}>
            <option value="">Choose…</option>
            {users.map(u => <option key={u.id} value={key(u)}>{u.name}{u.isAdmin ? ' (admin)' : ''}</option>)}
          </select>
        </label>
      </header>
      <main key={me}>
        {!me ? <div className="card">Choose a user at the top right to sign in. (This picker stands in for Entra ID sign-in during development.)</div>
          : item ? <ItemPage id={item[1]} />
          : path === '/record' ? <Record />
          : path === '/import' ? <Import />
          : path === '/reopen' ? <Reopen />
          : path === '/vocabulary' ? <Vocabulary />
          : <Dashboard />}
      </main>
    </>
  );
}
