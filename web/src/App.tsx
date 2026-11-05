import { useEffect, useRef, useState } from 'react';
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
import RetentionPage from './pages/Retention';
import WorkloadPage from './pages/Workload';
import DepartmentsPage from './pages/Departments';
import MinutesTemplatesPage from './pages/MinutesTemplates';
import NotificationsPage from './pages/Notifications';
import { Bell } from './Bell';
import SearchPage from './pages/Search';
import WidgetPage from './pages/Widget';
import Meet from './pages/Meet';
import Support from './pages/Support';
import SignOffCard from './SignOffCard';

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
  const [menu, setMenu] = useState(false); // the page list on a phone, folded away until asked for
  useEffect(() => setMenu(false), [hash]);
  // The header stays at the top of the screen; its height is shared so the pinned player sits just below it.
  const header = useRef<HTMLElement>(null);
  useEffect(() => {
    const el = header.current;
    if (!el || typeof ResizeObserver === 'undefined') return;
    const set = () => document.documentElement.style.setProperty('--header-h', `${el.offsetHeight}px`);
    set();
    const ro = new ResizeObserver(set); ro.observe(el);
    return () => ro.disconnect();
  }, []);
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
  const item = path.match(/^\/items\/([0-9a-f-]{36})(?:\?(.*))?$/);
  const itemQuery = new URLSearchParams(item?.[2] ?? '');
  const draft = path.match(/^\/dictate\/record\/([0-9a-f-]{36})$/);
  const carryOn = /^\/speech\/live\/([0-9a-f-]{36})$/.exec(path);
  const admin = me?.isAdmin;
  const support = me?.isSupport;

  return (
    <>
      <header className="top" ref={header}>
        <h1 className="brand"><a href="#/dictate" aria-label="VerbaFlow"><img src="/logo.svg" alt="" width="30" height="30" /><span>Verba<b>Flow</b></span></a></h1>
        <button className="menu-button" aria-expanded={menu} aria-controls="pages" onClick={() => setMenu(m => !m)}>{menu ? 'Close' : 'Menu'}</button>
        <nav id="pages" className={menu ? 'open' : ''}>
          <a href="#/dictate" className={path.startsWith('/dictate') ? 'on' : ''}>Dictate</a>
          <a href="#/speech" className={path.startsWith('/speech') ? 'on' : ''}>Speech</a>
          <a href="#/" className={path === '/' || path === '/record' || path === '/import' || path === '/meet' ? 'on' : ''}>Meeting</a>
          <a href="#/widget" className={path === '/widget' ? 'on' : ''}>Widget</a>
          <a href="#/search" className={path.startsWith('/search') ? 'on' : ''}>Search</a>
          {me && <MoreMenu path={path} admin={!!admin} support={!!support} lead={!!me?.runsDepartment} />}
        </nav>
        {me && <Bell path={path} />}
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
          {way === 'microsoft' && problem && <div className="actions"><button className="primary" onClick={() => signInWithMicrosoft()}>Sign in again</button></div>}
          {!way && info.microsoft && <><p>Use your Microsoft work account, the one you use for Outlook and Teams.</p>
            <div className="actions"><button className="primary" onClick={() => signInWithMicrosoft()}>Sign in with Microsoft</button></div></>}
          {!way && info.testUsers && <p className="note">{info.microsoft ? 'Or, on this computer only, pick a test user at the top right.' : 'Pick a test user at the top right. (Microsoft sign-in is not set up yet.)'}</p>}
          {!way && !info.microsoft && !info.testUsers && !problem && <p className="note">Sign-in is not set up yet. Your administrator needs to finish setting up VerbaFlow.</p>}
        </div>
          : item ? <ItemPage id={item[1]} at={itemQuery.get('at') ? Number(itemQuery.get('at')) : undefined} seg={itemQuery.get('seg') ?? undefined} />
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
          : path === '/minutes-templates' ? <MinutesTemplatesPage />
          : path === '/notifications' ? <NotificationsPage />
          : path === '/sign-off' ? <SignOffCard />
          : path === '/widget' ? <WidgetPage />
          : path === '/retention' ? <RetentionPage />
          : path === '/workload' ? <WorkloadPage />
          : path === '/departments' ? <DepartmentsPage />
          : path.startsWith('/search') ? <SearchPage key={path} query={new URLSearchParams(path.split('?')[1] ?? '')} />
          : path === '/meet' ? <Meet />
          : path === '/support' ? <Support />
          : <Dashboard key="meeting" mode="meeting" />}
      </main>
    </>
  );
}

/** Settings and admin pages, folded into one list so the header shows only the five main pages.
 *  Each person sees only what they may change: everyone their own sign-off (and the departments, to see who runs theirs), admins the rest. */
function MoreMenu({ path, admin, support, lead }: { path: string; admin: boolean; support: boolean; lead: boolean }) {
  const [open, setOpen] = useState(false);
  const box = useRef<HTMLDivElement>(null);
  useEffect(() => setOpen(false), [path]);
  useEffect(() => {
    if (!open) return;
    const away = (e: MouseEvent) => { if (box.current && !box.current.contains(e.target as Node)) setOpen(false); };
    const esc = (e: KeyboardEvent) => { if (e.key === 'Escape') setOpen(false); };
    addEventListener('mousedown', away); addEventListener('keydown', esc);
    return () => { removeEventListener('mousedown', away); removeEventListener('keydown', esc); };
  }, [open]);
  const pages: [string, string][] = [
    ...(admin ? [] : [...(lead ? [['/workload', 'Workload']] : []), ['/sign-off', 'Your sign-off'], ['/departments', 'Departments']] as [string, string][]),
    ...(admin ? [
      ['/workload', 'Workload'],
      ['/vocabulary', 'Vocabulary'], ['/saved-text', 'Saved text'], ['/letterhead', 'Letterhead and your sign-off'],
      ['/minutes-templates', 'Minutes templates'],
      ['/retention', 'Retention'], ['/departments', 'Departments'], ['/reopen', 'Reopen and delete requests'],
    ] as [string, string][] : []),
    ...(support ? [['/support', 'Support: errors'] as [string, string]] : []),
  ];
  const here = pages.some(([p]) => p === path);
  return (
    <div className="more" ref={box}>
      <button type="button" className={here ? 'on' : ''} aria-expanded={open} aria-haspopup="true" onClick={() => setOpen(o => !o)}>{admin ? 'Settings' : 'More'} ▾</button>
      {open && <div className="more-list" role="menu">
        {pages.map(([p, label]) => <a key={p} role="menuitem" href={`#${p}`} className={path === p ? 'on' : ''}>{label}</a>)}
      </div>}
    </div>
  );
}
