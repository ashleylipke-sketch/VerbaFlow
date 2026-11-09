import { useState } from 'react';
import { sendEmailCode, signInWithEmailCode } from './auth';

/** Sign-in with a code sent to an email address, for people without a Google or Microsoft account. No password. */
export default function EmailSignIn({ done }: { done: () => void }) {
  const [email, setEmail] = useState('');
  const [sentTo, setSentTo] = useState('');
  const [code, setCode] = useState('');
  const [name, setName] = useState('');
  const [error, setError] = useState('');
  const [busy, setBusy] = useState(false);

  const send = async () => {
    setBusy(true); setError('');
    const problem = await sendEmailCode(email.trim());
    setBusy(false);
    if (problem) setError(problem); else { setSentTo(email.trim()); setCode(''); }
  };
  const finish = async () => {
    setBusy(true); setError('');
    const problem = await signInWithEmailCode(sentTo, code, name);
    setBusy(false);
    if (problem) setError(problem); else done();
  };

  if (!sentTo) return (
    <form className="email-signin" onSubmit={e => { e.preventDefault(); if (email.trim()) send(); }}>
      <label className="field">Email address
        <input type="email" value={email} autoComplete="email" style={{ maxWidth: 360 }} onChange={e => setEmail(e.target.value)} placeholder="you@example.com" />
      </label>
      {error && <div className="error">{error}</div>}
      <div className="actions"><button type="submit" disabled={busy || !email.trim()}>{busy ? 'Sending…' : 'Email me a code'}</button></div>
    </form>
  );

  return (
    <form className="email-signin" onSubmit={e => { e.preventDefault(); if (code.replace(/\D/g, '').length === 6) finish(); }}>
      <p>We emailed a 6-digit code to <strong>{sentTo}</strong>. It lasts 10 minutes. If it does not arrive, look in the junk folder.</p>
      <label className="field">Code
        <input value={code} inputMode="numeric" autoComplete="one-time-code" maxLength={7} autoFocus onChange={e => setCode(e.target.value)} placeholder="123456"
          style={{ maxWidth: 160, letterSpacing: '0.15em', fontSize: '1.2em' }} />
      </label>
      <label className="field">Your name <span className="note">(the first time only: shown on your recordings; leave empty to keep the one you gave before)</span>
        <input value={name} autoComplete="name" maxLength={200} style={{ maxWidth: 360 }} onChange={e => setName(e.target.value)} />
      </label>
      {error && <div className="error">{error}</div>}
      <div className="actions">
        <button className="primary" type="submit" disabled={busy || code.replace(/\D/g, '').length !== 6}>{busy ? 'Signing in…' : 'Sign in'}</button>
        <button type="button" disabled={busy} onClick={send}>Send a new code</button>
        <button type="button" disabled={busy} onClick={() => { setSentTo(''); setError(''); }}>Use another address</button>
      </div>
    </form>
  );
}
