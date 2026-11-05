import { useEffect, useRef, useState } from 'react';
import { api, type NotificationList } from './api';

/** When a notice was made, in a few words ("5 min ago", "yesterday", "3 Nov"). */
export function ago(iso: string, now = Date.now()): string {
  const s = Math.max(0, (now - new Date(iso).getTime()) / 1000);
  if (s < 60) return 'just now';
  if (s < 3600) return `${Math.floor(s / 60)} min ago`;
  if (s < 86400) return `${Math.floor(s / 3600)} h ago`;
  if (s < 2 * 86400) return 'yesterday';
  return new Date(iso).toLocaleDateString('en-GB', { day: 'numeric', month: 'short' });
}

/** The bell at the top: how many notices are unread, and the latest ones in a list. Checks every minute. */
export function Bell({ path }: { path: string }) {
  const [list, setList] = useState<NotificationList | null>(null);
  const [open, setOpen] = useState(false);
  const box = useRef<HTMLDivElement>(null);

  const load = () => api.notifications().then(setList).catch(() => {});
  useEffect(() => { load(); const t = setInterval(load, 60_000); return () => clearInterval(t); }, []);
  useEffect(() => { setOpen(false); load(); }, [path]);
  useEffect(() => {
    if (!open) return;
    const away = (e: MouseEvent) => { if (box.current && !box.current.contains(e.target as Node)) setOpen(false); };
    const esc = (e: KeyboardEvent) => { if (e.key === 'Escape') setOpen(false); };
    addEventListener('mousedown', away); addEventListener('keydown', esc);
    return () => { removeEventListener('mousedown', away); removeEventListener('keydown', esc); };
  }, [open]);

  const unread = list?.unread ?? 0;
  const go = async (id: string, link: string, read: boolean) => {
    setOpen(false);
    if (!read) api.markRead(id).then(setList).catch(() => {});
    location.hash = link.replace(/^#/, '');
  };

  return (
    <div className="bell" ref={box}>
      <button type="button" aria-haspopup="true" aria-expanded={open} title="Notifications"
        aria-label={unread ? `Notifications, ${unread} unread` : 'Notifications'}
        onClick={() => { setOpen(o => !o); if (!open) load(); }}>
        <span aria-hidden="true">🔔</span>{unread > 0 && <span className="count">{unread > 99 ? '99+' : unread}</span>}
      </button>
      {open && <div className="bell-panel" role="dialog" aria-label="Notifications">
        <div className="bell-head"><strong>Notifications</strong>
          {unread > 0 && <button className="link" onClick={() => api.markRead().then(setList)}>Mark all as read</button>}</div>
        {!list || list.items.length === 0 ? <p className="note">Nothing yet. You'll be told here when something is assigned or returned to you,
          approved, ready, due, or waiting for you.</p>
          : <ul>{list.items.slice(0, 15).map(n => <li key={n.id} className={n.read ? '' : 'unread'}>
            <button className="bell-item" onClick={() => go(n.id, n.link, n.read)}>
              <span>{n.text}</span><span className="note">{ago(n.at)}</span></button></li>)}</ul>}
        <div className="bell-foot"><a href="#/notifications">See all, and choose what comes by email</a></div>
      </div>}
    </div>
  );
}
