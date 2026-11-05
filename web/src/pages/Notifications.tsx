import { useEffect, useState } from 'react';
import { api, type NotificationList, type NotificationSettings } from '../api';
import { ago } from '../Bell';

/** All of a person's notices, and which kinds also come by email. */
export default function NotificationsPage() {
  const [list, setList] = useState<NotificationList | null>(null);
  const [settings, setSettings] = useState<NotificationSettings | null>(null);
  const [message, setMessage] = useState('');
  const [error, setError] = useState('');

  useEffect(() => {
    api.notifications().then(setList).catch(e => setError(e.message));
    api.notificationSettings().then(setSettings).catch(e => setError(e.message));
  }, []);

  const toggle = async (key: string, on: boolean) => {
    if (!settings) return;
    setError(''); setMessage('');
    const emailOn = settings.groups.filter(g => g.key === key ? on : g.email).map(g => g.key);
    setSettings({ ...settings, groups: settings.groups.map(g => g.key === key ? { ...g, email: on } : g) }); // tick straight away
    try { setSettings(await api.setNotificationEmail(emailOn)); setMessage('Saved.'); } catch (e: any) { setError(e.message); }
  };
  const open = (id: string, link: string, read: boolean) => {
    if (!read) api.markRead(id).catch(() => {});
    location.hash = link.replace(/^#/, '');
  };

  return (
    <>
      <div className="card">
        <div className="row" style={{ justifyContent: 'space-between' }}>
          <h2>Notifications</h2>
          {list && list.unread > 0 && <button onClick={() => api.markRead().then(setList)}>Mark all as read</button>}
        </div>
        {error && <div className="error" role="alert">{error}</div>}
        {!list ? <p className="note">Loading…</p> : list.items.length === 0
          ? <p className="note">Nothing yet. You'll be told here when something is assigned or returned to you, approved, ready, due, or waiting for you.</p>
          : <ul className="notice-list">{list.items.map(n => <li key={n.id} className={n.read ? '' : 'unread'}>
            <button className="bell-item" onClick={() => open(n.id, n.link, n.read)}><span>{n.text}</span><span className="note">{ago(n.at)}</span></button>
          </li>)}</ul>}
      </div>
      {settings && <div className="card">
        <h3>By email</h3>
        {!settings.hasEmail
          ? <p className="note">VerbaFlow has no email address for you, so notices are only shown here.</p>
          : <p className="note">Every notice shows under the bell. Tick the ones you also want by email to {settings.email}.
            {!settings.emailReal && ' (Email is not set up on this VerbaFlow yet, so for now emails are only saved to its outbox folder.)'}</p>}
        {settings.groups.map(g => <label key={g.key} className="check">
          <input type="checkbox" checked={g.email} disabled={!settings.hasEmail} onChange={e => toggle(g.key, e.target.checked)} /> {g.label}</label>)}
        {message && <p className="ok" role="status">{message}</p>}
      </div>}
    </>
  );
}
