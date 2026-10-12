import { useEffect, useRef, useState } from 'react';
import { api, saveFile, type ExportCheck, type Letterhead } from './api';
import { letterheadNote } from './letterheadNote';

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
export function ExportMenu({ id, approved, dictate = false, documentType = null, onDone }:
  { id: string; approved: boolean; dictate?: boolean; documentType?: string | null; onDone: () => void }) {
  const [open, setOpen] = useState(false);
  const [parts, setParts] = useState<string[]>(exportParts.map(p => p.key));
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState('');
  const [panel, setPanel] = useState<'' | 'email' | 'share' | 'check'>('');
  const [fromApp, setFromApp] = useState(false);
  const [format, setFormat] = useState<Format>('pdf');
  const [info, setInfo] = useState('');
  const [pending, setPending] = useState<File | null>(null);
  const [mailTo, setMailTo] = useState('');
  const [mailNote, setMailNote] = useState('');
  const [mailSent, setMailSent] = useState('');
  const [check, setCheck] = useState<ExportCheck | null>(null);
  const box = useRef<HTMLDivElement>(null);
  const [letterhead, setLetterhead] = useState<Letterhead | null>(null);
  const letter = dictate && documentType === 'letter';

  // A dictated letter goes out on the letterhead, so the menu says which letterheads are set up.
  useEffect(() => {
    if (open && letter) api.letterhead().then(setLetterhead).catch(() => setLetterhead(null));
  }, [open, letter]);

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

  // Hands the file to the computer's or phone's share panel (WhatsApp, Teams, Outlook and others, whichever are installed).
  // A share request needs a fresh click. If the file took a moment to fetch, the browser may refuse the first request,
  // so a "Share now" button offers a fresh click; if that is refused too, the file is downloaded instead.
  const share = async (file: File, retried: boolean) => {
    try { await navigator.share({ files: [file], title: file.name }); setPending(null); setInfo(''); }
    catch (e: any) {
      if (e?.name === 'AbortError') return; // the person closed the share panel
      if (e?.name === 'NotAllowedError' && !retried) { setPending(file); setInfo('The file is ready. Click Share now to choose an app.'); return; }
      setPending(null);
      saveFile(file);
      setInfo(`Your browser would not hand this ${file.name.endsWith('.docx') ? 'Word file' : 'file'} to other apps, so it has been downloaded instead. ` +
        `Attach it in WhatsApp from your Downloads folder${file.name.endsWith('.docx') ? ', or try again with As PDF' : ''}.`);
    }
  };
  const startShare = () => act(async () => {
    setInfo(''); setPending(null);
    const file = await api.exportFile(id, format, parts, 'share');
    if (!navigator.canShare?.({ files: [file] })) {
      saveFile(file);
      setInfo('This browser cannot pass files to other apps, so the file has been downloaded instead. Attach it in WhatsApp from your Downloads folder.');
      return;
    }
    await share(file, false);
  });
  const formatPicker = <select aria-label="Format" value={format} onChange={e => { setFormat(e.target.value as Format); setPending(null); setInfo(''); }}>
    <option value="pdf">As PDF</option><option value="docx">As Word</option></select>;

  return (
    <div className="menu" ref={box}>
      <button className="primary" aria-haspopup="true" aria-expanded={open} onClick={() => setOpen(o => !o)}>Export ▾</button>
      {open && <div className="menu-panel" role="dialog" aria-label="Export">
        {dictate ? <><p className="note">Exports the text of the dictation, with its details and {approved ? 'Approved' : 'Draft'} marker.</p>
          <p className="note" data-testid="letterhead-note">{letterheadNote(documentType, letterhead)}</p></> : <><fieldset>
          <legend>Include</legend>
          {exportParts.map(p => <label key={p.key} className="check">
            <input type="checkbox" checked={parts.includes(p.key)} onChange={() => toggle(p.key)} /> {p.label}</label>)}
        </fieldset>
        <p className="note">The title, details and {approved ? 'Approved' : 'Draft'} marker are always included. Each file's fingerprint is recorded, so a copy can be checked later.</p></>}
        {none && <p className="error">Tick at least one part.</p>}
        <div className="menu-items">
          <button disabled={busy || none} onClick={() => act(() => api.exportMinutes(id, 'docx', parts))}>Download Word</button>
          <button disabled={busy || none} onClick={() => act(() => api.exportMinutes(id, 'pdf', parts))}>Download PDF</button>
          <button disabled={busy || none} onClick={preview}>Preview PDF</button>
          <button disabled={none} aria-expanded={panel === 'email'} onClick={() => { setInfo(''); setPanel(p => p === 'email' ? '' : 'email'); }}>Email…</button>
          <button disabled={none} aria-expanded={panel === 'share'} onClick={() => { setInfo(''); setPending(null); setPanel(p => p === 'share' ? '' : 'share'); }}>Share… (WhatsApp, Teams and more)</button>
          <button aria-expanded={panel === 'check'} onClick={() => setPanel(p => p === 'check' ? '' : 'check')}>Check an exported file…</button>
        </div>
        {panel === 'email' && !fromApp && <div className="menu-sub">
          <p className="note">Opens a new email in your own email program, from your own account, with the {dictate ? 'dictation' : 'minutes'} attached. Choose the people there.</p>
          <div className="actions">{formatPicker}
            <button disabled={busy || none} onClick={() => act(async () => {
              saveFile(await api.exportFile(id, format, parts, 'draft'));
              setInfo('An email file has been downloaded. Open it (click it in your browser\'s downloads) and it appears as a new email in Outlook.');
            })}>Open a new email</button></div>
          {info && <p className="ok" role="status">{info}</p>}
          <p className="note"><button className="link" onClick={() => { setInfo(''); setFromApp(true); }}>Or send it from VerbaFlow instead</button></p>
        </div>}
        {panel === 'email' && fromApp && <div className="menu-sub">
          <div className="field"><label>To (up to 10 addresses, separated by commas)<br />
            <input type="text" value={mailTo} onChange={e => { setMailTo(e.target.value); setMailSent(''); }} placeholder="name@example.com" /></label></div>
          <div className="field"><label>Message (optional)<br />
            <textarea rows={3} maxLength={2000} value={mailNote} onChange={e => setMailNote(e.target.value)} /></label></div>
          <div className="actions">{formatPicker}
            <button disabled={busy || none || !mailTo.trim()} onClick={() => act(async () => {
              setMailSent('');
              const r = await api.emailMinutes(id, format, mailTo, mailNote, parts);
              setMailSent(r.message); setMailTo(''); setMailNote('');
            })}>{busy ? 'Sending…' : 'Send email'}</button></div>
          <p className="note">Sent by VerbaFlow as an attachment; the history records who it went to. Replies go to you.</p>
          {mailSent && <p className="ok" role="status">{mailSent}</p>}
          <p className="note"><button className="link" onClick={() => setFromApp(false)}>Use my own email program instead</button></p>
        </div>}
        {panel === 'share' && <div className="menu-sub">
          <p className="note">Opens your computer's or phone's share panel. WhatsApp shows there if it is installed.</p>
          <div className="actions">{formatPicker}
            {pending
              ? <button className="primary" onClick={() => act(() => share(pending, true))}>Share now</button>
              : <button disabled={busy || none} onClick={startShare}>Share</button>}</div>
          {info && <p className="ok" role="status">{info}</p>}
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

