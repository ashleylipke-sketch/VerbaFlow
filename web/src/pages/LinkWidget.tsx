import { useEffect, useState } from 'react';
import { api } from '../api';
import { isIndividual } from '../whoami';

/**
 * Links a VerbaFlow Widget (browser extension, Windows, phone keyboard) to this personal account. The Widget showed a
 * code and opened this page; the person checks the code matches and agrees.
 */
export default function LinkWidget({ code: given }: { code: string }) {
  const [code, setCode] = useState(given);
  const [waiting, setWaiting] = useState<{ device: string; code: string } | null>(null);
  const [error, setError] = useState('');
  const [done, setDone] = useState('');
  const [busy, setBusy] = useState(false);
  const look = (c: string) => {
    setError(''); setWaiting(null);
    if (c.replace(/[^a-z0-9]/gi, '').length !== 8) return;
    api.get<{ device: string; code: string }>(`/widget-link/${encodeURIComponent(c)}`).then(setWaiting).catch(e => setError(e.message));
  };
  useEffect(() => { if (given) look(given); }, [given]);

  if (!isIndividual()) return <div className="card"><h2>Link a Widget</h2>
    <p>You signed in with a work account, so the Widget signs you in itself: in the Widget's settings, choose <strong>Sign in with Microsoft</strong>.</p></div>;

  return (
    <div className="card" style={{ maxWidth: 560 }}>
      <h2>Link a Widget to your account</h2>
      {done ? <p className="ok" role="status">Done. The {done} is now signed in as you. You can close this page and go back to it.</p> : <>
        <p className="note">Only do this if you started it yourself, just now, in the VerbaFlow Widget on your own computer or phone. Someone else's code would let
          them use VerbaFlow as you.</p>
        <label className="field">Code shown in the Widget
          <input value={code} maxLength={9} autoComplete="off" style={{ textTransform: 'uppercase', fontSize: '1.3em', letterSpacing: '0.1em', maxWidth: 200 }}
            onChange={e => { setCode(e.target.value); look(e.target.value); }} placeholder="ABCD-EFGH" />
        </label>
        {error && <div className="error">{error}</div>}
        {waiting && <>
          <p>The <strong>{waiting.device}</strong> showing <strong>{waiting.code}</strong> will be signed in as you.</p>
          <div className="actions">
            <button className="primary" disabled={busy} onClick={async () => {
              setBusy(true); setError('');
              try { const r = await api.post(`/widget-link/${encodeURIComponent(waiting.code)}/approve`, {}); setDone(r.device); }
              catch (e: any) { setError(e.message); } finally { setBusy(false); }
            }}>Link it</button>
            <a className="btn" href="#/dictate">Cancel</a>
          </div>
        </>}
      </>}
    </div>
  );
}
