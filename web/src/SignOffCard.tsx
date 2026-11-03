import { useEffect, useState } from 'react';
import { api, type SignOff } from './api';

/** Your own sign-off: printed under the letters you dictate when they are exported. */
export default function SignOffCard({ onSaved }: { onSaved?: () => void }) {
  const [s, setS] = useState<SignOff | null>(null);
  const [form, setForm] = useState({ on: false, closing: '', name: '', title: '' });
  const [picture, setPicture] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const [message, setMessage] = useState('');
  const [error, setError] = useState('');

  const show = (x: SignOff) => {
    setS(x);
    setForm({ on: x.on, closing: x.closing, name: x.name, title: x.title ?? '' });
  };
  useEffect(() => { api.signOff().then(show).catch(e => setError(e.message)); }, []);
  // The picture is fetched with the sign-in headers, then shown from memory.
  useEffect(() => {
    if (!s?.hasPicture) { setPicture(null); return; }
    let url: string | null = null;
    api.signaturePicture().then(b => { url = URL.createObjectURL(b); setPicture(url); }).catch(() => setPicture(null));
    return () => { if (url) URL.revokeObjectURL(url); };
  }, [s?.picture]);

  const run = async (fn: () => Promise<SignOff>, done: string) => {
    setBusy(true); setError(''); setMessage('');
    try { show(await fn()); setMessage(done); onSaved?.(); } catch (e: any) { setError(e.message); } finally { setBusy(false); }
  };

  if (!s) return error ? <div className="card"><div className="error">{error}</div></div> : null;
  const changed = form.on !== s.on || form.closing !== s.closing || form.name !== s.name || form.title !== (s.title ?? '');
  return (
    <div className="card" id="signoff">
      <h2>Your sign-off</h2>
      <p className="note">Printed at the end of the letters and emails you dictate when they are exported as Word or PDF. If you said a closing yourself
        (for example "Yours faithfully"), yours is kept and your name and title go under it. Your signature picture is only added once the letter or email is approved.
        Only you can see and change this.</p>
      {error && <div className="error">{error}</div>}
      {message && <div className="ok" role="status">{message}</div>}
      <label className="row"><input type="checkbox" checked={form.on} disabled={busy}
        onChange={e => setForm({ ...form, on: e.target.checked })} /> Add my sign-off to my letters and emails</label>
      <div className="pair" style={{ flexWrap: 'wrap', marginTop: 8 }}>
        <label className="field">Closing
          <input value={form.closing} maxLength={100} placeholder="Yours sincerely," disabled={busy}
            onChange={e => setForm({ ...form, closing: e.target.value })} /></label>
        <label className="field">Name under the signature
          <input value={form.name} maxLength={100} disabled={busy} onChange={e => setForm({ ...form, name: e.target.value })} /></label>
        <label className="field">Job title (optional)
          <input value={form.title} maxLength={100} placeholder="Partner" disabled={busy}
            onChange={e => setForm({ ...form, title: e.target.value })} /></label>
      </div>
      <div className="actions">
        <button className="primary" disabled={busy || !changed}
          onClick={() => run(() => api.saveSignOff(form), form.on ? 'Sign-off saved. Letters exported from now on end with it.' : 'Saved. Your letters will not have a sign-off.')}>
          Save</button>
      </div>

      <h3>Signature picture</h3>
      {picture ? <p><img src={picture} alt="Your signature" style={{ maxHeight: 70, maxWidth: '100%', background: '#fff', border: '1px solid var(--line, #ddd)', padding: 4 }} /></p>
        : <p className="note">None. Sign a piece of white paper, take a photo or scan it, crop it close around the signature, and add it here (PNG or JPEG, up to 1 MB).</p>}
      <div className="actions">
        <label className={'btn' + (picture ? '' : ' primary')}>{picture ? 'Replace…' : 'Add a picture…'}
          <input type="file" hidden accept=".png,.jpg,.jpeg,image/png,image/jpeg" disabled={busy}
            onChange={e => { const f = e.target.files?.[0]; e.target.value = ''; if (f) run(() => api.uploadSignature(f), 'Signature picture saved.'); }} /></label>
        {picture && <button className="danger" disabled={busy} onClick={() => run(() => api.removeSignature(), 'Signature picture removed.')}>Remove</button>}
      </div>
    </div>
  );
}
