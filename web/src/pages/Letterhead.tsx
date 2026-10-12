import { useEffect, useState } from 'react';
import { api, saveFile, type Letterhead, type LetterheadFile, type UserView } from '../api';

type Kind = 'word' | 'pdf';

/** The organisation's letterhead, used when a dictated letter is exported as Word or PDF. */
export default function LetterheadPage() {
  const [l, setL] = useState<Letterhead | null>(null);
  const [me, setMe] = useState<UserView | null>(null);
  const [users, setUsers] = useState<UserView[]>([]);
  const [space, setSpace] = useState({ topFirstMm: 55, topNextMm: 25, bottomMm: 30, sideMm: 25 });
  const [busy, setBusy] = useState(false);
  const [message, setMessage] = useState('');
  const [error, setError] = useState('');
  const [confirming, setConfirming] = useState<Kind | null>(null);

  const show = (x: Letterhead) => { setL(x); setSpace({ topFirstMm: x.pdfTopFirstMm, topNextMm: x.pdfTopNextMm, bottomMm: x.pdfBottomMm, sideMm: x.pdfSideMm }); };
  useEffect(() => {
    api.letterhead().then(show).catch(e => setError(e.message));
    api.me().then(setMe);
    api.users().then(setUsers).catch(() => {});
  }, []);
  const admin = me?.isAdmin === true;

  const run = async (fn: () => Promise<Letterhead | void>, done: string) => {
    setBusy(true); setError(''); setMessage('');
    try { const x = await fn(); if (x) show(x); setMessage(done); } catch (e: any) { setError(e.message); } finally { setBusy(false); }
  };
  const upload = (kind: Kind, file: File | undefined) => file && run(() => api.uploadLetterhead(kind, file),
    `${kind === 'word' ? 'Word' : 'PDF'} letterhead saved. Letters exported from now on use it.`);
  const remove = (kind: Kind) => run(async () => { setConfirming(null); return api.removeLetterhead(kind); },
    `${kind === 'word' ? 'Word' : 'PDF'} letterhead removed. Letters will use a plain page.`);
  const preview = () => run(async () => { window.open(URL.createObjectURL(await api.letterheadFile('sample?format=pdf&preview=true')), '_blank'); }, '');
  const sampleWord = () => run(async () => { saveFile(new File([await api.letterheadFile('sample?format=docx')], 'sample letter.docx')); }, '');
  const download = (kind: Kind, f: LetterheadFile) => run(async () => { saveFile(new File([await api.letterheadFile(kind)], f.fileName)); }, '');

  const who = (id: string) => users.find(u => u.id === id)?.name ?? 'an administrator';
  const when = (d: string) => new Date(d).toLocaleDateString('en-GB', { day: 'numeric', month: 'long', year: 'numeric' });

  const fileRow = (kind: Kind, f: LetterheadFile | null | undefined) => (
    <>
      {f ? <p><strong>{f.fileName}</strong> <span className="note">· added by {who(f.uploadedBy)} on {when(f.uploadedAt)} · {Math.max(1, Math.round(f.sizeBytes / 1024))} KB</span></p>
        : <p className="note">None yet. Letters use a plain {kind === 'word' ? 'Word' : 'PDF'} page.</p>}
      {admin && <div className="actions">
        <label className={'btn' + (f ? '' : ' primary')}>{f ? 'Replace…' : 'Choose a file…'}
          <input type="file" hidden accept={kind === 'word' ? '.docx,.dotx' : '.pdf'} disabled={busy}
            onChange={e => { upload(kind, e.target.files?.[0]); e.target.value = ''; }} /></label>
        {f && <button disabled={busy} onClick={() => download(kind, f)}>Download</button>}
        {f && confirming !== kind && <button className="danger" disabled={busy} onClick={() => setConfirming(kind)}>Remove</button>}
        {confirming === kind && <span className="row">Remove it? <button className="danger" onClick={() => remove(kind)}>Yes, remove</button><button onClick={() => setConfirming(null)}>No</button></span>}
      </div>}
    </>
  );

  const mm = (key: keyof typeof space, label: string) => (
    <label className="field">{label}
      <span className="row"><input type="number" min={5} max={120} value={space[key]} disabled={!admin}
        onChange={e => setSpace({ ...space, [key]: Number(e.target.value) })} style={{ width: 90 }} /> mm</span></label>
  );

  return (
    <div className="card">
      <h2>Letterhead</h2>
      <p className="note">When a dictated letter is exported, it is put on your letterhead with the date and "Our ref" at the top. Letters that are not approved yet
        say so in red above the date. Word documents and emails are not affected.</p>
      {error && <div className="error">{error}</div>}
      {message && <div className="ok" role="status">{message}</div>}
      {me && !admin && <p className="note">Only administrators can change the letterhead.</p>}

      <h3>For Word</h3>
      <p className="note">Use your usual Word letterhead (.docx). Its header, footer, page size, margins and fonts are kept, so put the logo and address in the
        header and footer. The letter replaces whatever is typed in the page itself. To keep something in the page (for example a line under the logo), type
        <code> {'{{letter}}'} </code> where the letter should go.</p>
      {l && fileRow('word', l.word)}

      <h3>For PDF</h3>
      <p className="note">A PDF of your letterhead. Page 1 is printed behind the first page of a letter; page 2, if it has one, behind the pages after.
        The spaces below keep the letter's text clear of what is printed on it.</p>
      {l && fileRow('pdf', l.pdf)}
      <div className="pair" style={{ flexWrap: 'wrap', marginTop: 8 }}>
        {mm('topFirstMm', 'Space at the top of page 1')}
        {mm('topNextMm', 'Space at the top of later pages')}
        {mm('bottomMm', 'Space at the bottom')}
        {mm('sideMm', 'Space at each side')}
      </div>
      {admin && <div className="actions"><button disabled={busy} onClick={() => run(() => api.letterheadSpace(space), 'Spaces saved.')}>Save spaces</button></div>}

      <h3>Check how it looks</h3>
      <div className="actions">
        <button className="primary" disabled={busy} onClick={preview}>Preview a sample letter (PDF)</button>
        <button disabled={busy} onClick={sampleWord}>Download a sample letter (Word)</button>
      </div>
    </div>
  );
}
