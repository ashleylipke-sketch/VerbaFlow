import { useEffect, useState } from 'react';
import { api } from '../api';

/** Administrators: which email service VerbaFlow uses, and a test email to check it arrives. */
export default function EmailServicePage() {
  const [info, setInfo] = useState<{ real: boolean; sender: string | null; yourEmail: string | null } | null>(null);
  const [to, setTo] = useState('');
  const [busy, setBusy] = useState(false);
  const [message, setMessage] = useState('');
  const [error, setError] = useState('');

  useEffect(() => { api.emailService().then(i => { setInfo(i); setTo(i.yourEmail ?? ''); }).catch(e => setError(e.message)); }, []);

  const send = async () => {
    setBusy(true); setError(''); setMessage('');
    try {
      const r = await api.testEmail(to.trim());
      setMessage(r.outcome === 'HandedOver' ? `Azure accepted the test email and passed it on for delivery to ${r.to}. Check that inbox, and the junk folder.`
        : r.outcome === 'StillWaiting' ? `Azure accepted the test email for ${r.to} but had not finished after a minute. Check that inbox in a few minutes, and the junk folder.`
        : `Email is not set up yet, so the test email was saved in VerbaFlow's outbox folder (data\\outbox) instead of being sent.`);
    } catch (e: any) { setError(e.message); }
    setBusy(false);
  };

  return (
    <div className="card">
      <h2>Email</h2>
      {error && <div className="error" role="alert">{error}</div>}
      {!info ? !error && <p className="note">Loading…</p> : <>
        <p>{info.real
          ? <>VerbaFlow sends email through Azure Communication Services, from <b>{info.sender}</b>.</>
          : <>Email is <b>not set up yet</b>. Nothing leaves this computer: sign-in codes, notifications and emailed minutes are saved as files in VerbaFlow's outbox folder. The guide <i>docs/azure-email-setup.md</i> says how to set it up.</>}</p>
        <p className="note">Sign-in codes, notifications and emailed minutes all go out this way.</p>
        <h3>Send a test email</h3>
        <div className="row">
          <input type="email" aria-label="Send the test email to" value={to} onChange={e => setTo(e.target.value)} placeholder="Email address" style={{ maxWidth: 360, flex: 1 }} />
          <button onClick={send} disabled={busy || !to.trim()}>{busy ? 'Sending… (this can take up to a minute)' : 'Send a test email'}</button>
        </div>
        {message && <p className="ok" role="status">{message}</p>}
      </>}
    </div>
  );
}
