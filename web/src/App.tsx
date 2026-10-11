import { useEffect, useState } from 'react';
import { api, getUser, setUser, type UserView } from './api';
import Dashboard from './pages/Dashboard';
import Record from './pages/Record';
import Import from './pages/Import';
import ItemPage from './pages/ItemPage';
import Reopen from './pages/Reopen';
import Vocabulary from './pages/Vocabulary';
import SavedTextPage from './pages/SavedText';
import Support from './pages/Support';

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
  const support = users.find(u => key(u) === me)?.isSupport;

  return (
    <>
      <header className="top">
        <h1>VerbaFlow · Speak</h1>
        <nav>
          <a href="#/dictate" className={path.startsWith('/dictate') ? 'on' : ''}>Dictate</a>
          <a href="#/" className={path === '/' || path === '/record' || path === '/import' ? 'on' : ''}>Meeting</a>
          <a href="#/vocabulary" className={path === '/vocabulary' ? 'on' : ''}>Vocabulary</a>
          <a href="#/saved-text" className={path === '/saved-text' ? 'on' : ''}>Saved text</a>
          {support && <a href="#/support" className={path === '/support' ? 'on' : ''}>Support: errors</a>}
          {admin && <a href="#/reopen" className={path === '/reopen' ? 'on' : ''}>Reopen and delete requests</a>}
        </nav>
        <label className="note" title="Development stand-in for Microsoft Entra ID sign-in">
          Signed in as{' '}
          <select value={me} onChange={e => { setUser(e.target.value); setMe(e.target.value); }}>
            <option value="">Choose…</option>
            {users.map(u => <option key={u.id} value={key(u)}>{u.name}{u.isAdmin ? ' (admin)' : ''}{u.isSupport ? ' (support)' : ''}</option>)}
          </select>
        </label>
      </header>
      <main key={me}>
        {!me ? <div className="card">Choose a user at the top right to sign in. (This picker stands in for Entra ID sign-in during development.)</div>
          : item ? <ItemPage id={item[1]} />
          : path === '/record' ? <Record mode="meeting" />
          : path === '/import' ? <Import mode="meeting" />
          : path === '/dictate/record' ? <Record key="dictate" mode="dictate" />
          : path === '/dictate/import' ? <Import key="dictate" mode="dictate" />
          : path === '/dictate' ? <Dashboard key="dictate" mode="dictate" />
          : path === '/reopen' ? <Reopen />
          : path === '/vocabulary' ? <Vocabulary />
          : path === '/saved-text' ? <SavedTextPage />
          : path === '/support' ? <Support />
          : <Dashboard key="meeting" mode="meeting" />}
      </main>
    </>
  );
}
