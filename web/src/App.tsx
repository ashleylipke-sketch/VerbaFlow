import { useEffect, useState } from 'react';
import { api, type UserView } from './api';
import { getTestUser, microsoftName, setTestUser, signedIn, signInInfo, signInProblem, signInWithMicrosoft, signOut } from './auth';
import Dashboard from './pages/Dashboard';
import Record from './pages/Record';
import Dictate from './pages/Dictate';
import Speech from './pages/Speech';
import Import from './pages/Import';
import ItemPage from './pages/ItemPage';
import Reopen from './pages/Reopen';
import Vocabulary from './pages/Vocabulary';
import SavedTextPage from './pages/SavedText';
import LetterheadPage from './pages/Letterhead';
import WidgetPage from './pages/Widget';
import Meet from './pages/Meet';
import Support from './pages/Support';

function useHash() {
  const [h, setH] = useState(location.hash || '#/');
  useEffect(() => { const f = () => setH(location.hash || '#/'); addEventListener('hashchange', f); return () => removeEventListener('hashchange', f); }, []);
  return h;
}

export default function App() {
  const hash = useHash();
  const info = signInInfo();
  const [users, setUsers] = useState<UserView[]>([]);
  const [me, setMe] = useState<UserView | null>(null);
  const [way, setWay] = useState(signedIn());
  const [problem, setProblem] = useState(signInProblem());
  // The test users to pick from (only on the owner's computer); the server lists them to anyone while test users are on.
  useEffect(() => {
    if (info.testUsers) fetch('/api/users', { headers: { 'X-Dev-User': 'alice' } }).then(r => r.json()).then(setUsers).catch(() => setUsers([]));
  }, []);
  useEffect(() => {
    setMe(null);
    if (way) api.me().then(setMe).catch(e => { setProblem(e.message); });
  }, [way, getTestUser()]);
  const key = (u: UserView) => u.email.split('@')[0];
  const pickTest = (u: string) => { setTestUser(u); setProblem(''); setWay(signedIn()); };
  const path = hash.replace(/^#/, '');
  const item = path.match(/^\/items\/([0-9a-f-]{36})$/);
  const draft = path.match(/^\/dictate\/record\/([0-9a-f-]{36})$/);
  const carryOn = /^\/speech\/live\/([0-9a-f-]{36})$/.exec(path);
  const admin = me?.isAdmin;
  const support = me?.isSupport;

  return (
    <>
      <header className="top">
        <h1>VerbaFlow · Speak</h1>
        <nav>
          <a href="#/dictate" className={path.startsWith('/dictate') ? 'on' : ''}>Dictate</a>
          <a href="#/speech" className={path.startsWith('/speech') ? 'on' : ''}>Speech</a>
          <a href="#/" className={path === '/' || path === '/record' || path === '/import' ? 'on' : ''}>Meeting</a>
          <a href="#/meet" className={path === '/meet' ? 'on' : ''}>Meet</a>
          <a href="#/vocabulary" className={path === '/vocabulary' ? 'on' : ''}>Vocabulary</a>
          <a href="#/saved-text" className={path === '/saved-text' ? 'on' : ''}>Saved text</a>
          <a href="#/letterhead" className={path === '/letterhead' ? 'on' : ''}>Letterhead</a>
          <a href="#/widget" className={path === '/widget' ? 'on' : ''}>Widget</a>
          {support && <a href="#/support" className={path === '/support' ? 'on' : ''}>Support: errors</a>}
          {admin && <a href="#/reopen" className={path === '/reopen' ? 'on' : ''}>Reopen and delete requests</a>}
        </nav>
        <span className="note signin">
          {way === 'microsoft'
            ? <>Signed in as {me?.name || microsoftName()}{me?.isAdmin ? ' (admin)' : ''} · <a href="#" onClick={e => { e.preventDefault(); signOut(); }}>Sign out</a></>
            : info.testUsers
              ? <label title="Test users: only on this computer, while VerbaFlow runs in Development">
                {way ? 'Test user ' : 'Test sign-in '}
                <select value={way ? getTestUser() : ''} onChange={e => pickTest(e.target.value)}>
                  <option value="">Choose…</option>
                  {users.map(u => <option key={u.id} value={key(u)}>{u.name}{u.isAdmin ? ' (admin)' : ''}{u.isSupport ? ' (support)' : ''}</option>)}
                </select>
                {info.microsoft && <> · <a href="#" onClick={e => { e.preventDefault(); signInWithMicrosoft(); }}>Sign in with Microsoft</a></>}
              </label>
              : null}
        </span>
      </header>
      <main key={me?.id ?? ''}>
        {!way || !me ? <div className="card signin-card">
          <h2>Sign in to VerbaFlow</h2>
          {problem && <div className="error">{problem}</div>}
          {way && !me && !problem && <p className="note">Signing in…</p>}
          {!way && info.microsoft && <><p>Use your Microsoft work account, the one you use for Outlook and Teams.</p>
            <div className="actions"><button className="primary" onClick={() => signInWithMicrosoft()}>Sign in with Microsoft</button></div></>}
          {!way && info.testUsers && <p className="note">{info.microsoft ? 'Or, on this computer only, pick a test user at the top right.' : 'Pick a test user at the top right. (Microsoft sign-in is not set up yet.)'}</p>}
          {!way && !info.microsoft && !info.testUsers && !problem && <p className="note">Sign-in is not set up yet. Your administrator needs to finish setting up VerbaFlow.</p>}
        </div>
          : item ? <ItemPage id={item[1]} />
          : path === '/record' ? <Record mode="meeting" />
          : path === '/import' ? <Import mode="meeting" />
          : draft ? <Dictate key={draft[1]} draftId={draft[1]} />
          : path === '/dictate/record' ? <Dictate key="new" />
          : path === '/dictate/import' ? <Import key="dictate" mode="dictate" />
          : path === '/dictate' ? <Dashboard key="dictate" mode="dictate" />
          : carryOn ? <Speech key={carryOn[1]} draftId={carryOn[1]} />
          : path === '/speech/live' ? <Speech key="new" />
          : path === '/speech' ? <Dashboard key="speech" mode="speech" />
          : path === '/reopen' ? <Reopen />
          : path === '/vocabulary' ? <Vocabulary />
          : path === '/saved-text' ? <SavedTextPage />
          : path === '/letterhead' ? <LetterheadPage />
          : path === '/widget' ? <WidgetPage />
          : path === '/meet' ? <Meet />
          : path === '/support' ? <Support />
          : <Dashboard key="meeting" mode="meeting" />}
      </main>
    </>
  );
}
