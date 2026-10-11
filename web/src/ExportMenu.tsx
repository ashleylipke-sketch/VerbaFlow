import { useEffect, useRef, useState } from 'react';
import { api, type ExportCheck } from './api';

/** The parts a person can tick. The title, Approved/Draft marker and details are always in the file. */
export const exportParts = [
  { key: 'summary', label: 'Summary' },
  { key: 'minutes', label: 'Minutes' },
  { key: 'actions', label: 'Action points' },
  { key: 'transcript', label: 'Transcript' },
  { key: 'tone', label: 'Tone' },
] as const;

type Format = 'docx' | 'pdf';

/** The Export button at the top of a meeting: opens a menu to pick what goes in, then download, preview or email it. */
export function ExportMenu({ id, approved, onDone }: { id: string; approved: boolean; onDone: () => void }) {
  const [open, setOpen] = useState(false);
  const [parts, setParts] = useState<string[]>(exportParts.map(p => p.key));
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState('');
  const [panel, setPanel] = useState<'' | 'email' | 'check'>('');
  const [mailTo, setMailTo] = useState('');
  const [mailNote, setMailNote] = useState('');
  const [mailFormat, setMailFormat] = useState<Format>('pdf');
  const [mailSent, setMailSent] = useState('');
  const [check, setCheck] = useState<ExportCheck | null>(null);
  const box = useRef<HTMLDivElement>(null);

  // Close when clicking elsewhere or pressing Escape.
  useEffect(() => {
    if (!open) return;
    const click = (e: MouseEvent) => { if (box.current && !box.current.contains(e.target as Node)) setOpen(false); };
    const key = (e: KeyboardEvent) => { if (e.key === 'Escape') setOpen(false); };
    document.addEventListener('mousedown', click); document.addEventListener('keydown', key);
    return () => { document.removeEventListener('mousedown', click); document.removeEventListener('keydown', key); };
  }, [open]);

  const none = parts.length === 0;
  const toggle = (k: string) => setParts(p => p.includes(k) ? p.filter(x => x !== k) : exportParts.map(x => x.key).filter(x => x === k || p.includes(x)));
  const act = async (fn: () => Promise<unknown>) => {
    setError(''); setBusy(true);
    try { await fn(); onDone(); } catch (e: any) { setError(e.message); } finally { setBusy(false); }
  };
  const preview = () => {
    // The tab is opened straight away (browsers block tabs opened later), then filled once the PDF arrives.
    const tab = window.open('', '_blank');
    act(async () => {
      try {
        const url = URL.createObjectURL(await api.previewMinutes(id, parts));
        if (tab) tab.location.href = url; else window.open(url, '_blank');
        setTimeout(() => URL.revokeObjectURL(url), 60_000);
      } catch (e) { tab?.close(); throw e; }
    });
  };

  return (
    <div className="menu" ref={box}>
      <button className="primary" aria-haspopup="true" aria-expanded={open} onClick={() => setOpen(o => !o)}>Export ▾</button>
      {open && <div className="menu-panel" role="dialog" aria-label="Export">
        <fieldset>
          <legend>Include</legend>
          {exportParts.map(p => <label key={p.key} className="check">
            <input type="checkbox" checked={parts.includes(p.key)} onChange={() => toggle(p.key)} /> {p.label}</label>)}
        </fieldset>
        <p className="note">The title, details and {approved ? 'Approved' : 'Draft'} marker are always included. Each file's fingerprint is recorded, so a copy can be checked later.</p>
        {none && <p className="error">Tick at least one part.</p>}
        <div className="menu-items">
          <button disabled={busy || none} onClick={() => act(() => api.exportMinutes(id, 'docx', parts))}>Download Word</button>
          <button disabled={busy || none} onClick={() => act(() => api.exportMinutes(id, 'pdf', parts))}>Download PDF</button>
          <button disabled={busy || none} onClick={preview}>Preview PDF</button>
          <button disabled={none} aria-expanded={panel === 'email'} onClick={() => setPanel(p => p === 'email' ? '' : 'email')}>Email…</button>
          <button aria-expanded={panel === 'check'} onClick={() => setPanel(p => p === 'check' ? '' : 'check')}>Check an exported file…</button>
        </div>
        {panel === 'email' && <div className="menu-sub">
          <div className="field"><label>To (up to 10 addresses, separated by commas)<br />
            <input type="text" value={mailTo} onChange={e => { setMailTo(e.target.value); setMailSent(''); }} placeholder="name@example.com" /></label></div>
          <div className="field"><label>Message (optional)<br />
            <textarea rows={3} maxLength={2000} value={mailNote} onChange={e => setMailNote(e.target.value)} /></label></div>
          <div className="actions">
            <select aria-label="Email format" value={mailFormat} onChange={e => setMailFormat(e.target.value as Format)}>
              <option value="pdf">As PDF</option><option value="docx">As Word</option></select>
            <button disabled={busy || none || !mailTo.trim()} onClick={() => act(async () => {
              setMailSent('');
              const r = await api.emailMinutes(id, mailFormat, mailTo, mailNote, parts);
              setMailSent(r.message); setMailTo(''); setMailNote('');
            })}>{busy ? 'Sending…' : 'Send email'}</button></div>
          <p className="note">Sent as an attachment. Replies go to you.</p>
          {mailSent && <p className="ok" role="status">{mailSent}</p>}
        </div>}
        {panel === 'check' && <div className="menu-sub">
          <p className="note">Choose a Word or PDF file you were sent to see whether it is exactly as VerbaFlow exported it.</p>
          <input type="file" aria-label="File to check" accept=".pdf,.docx" onChange={e => {
            const f = e.target.files?.[0]; e.target.value = '';
            if (f) act(async () => setCheck(await api.checkExport(id, f)));
          }} />
          {check && <p className={check.matches ? 'ok' : 'error'} role="status">{check.message}</p>}
        </div>}
        {error && <p className="error" role="alert">{error}</p>}
      </div>}
    </div>
  );
}
