import { useEffect, useState } from 'react';
import { api, type WidgetPolicy, type WidgetUse } from '../api';

const OUTCOMES: Record<string, string> = { accepted: 'Accepted', cancelled: 'Cancelled', open: 'In use', 'not finished': 'Not finished' };

/** Widget: speaking into any box on any website (browser extension) or any Windows program (Widget for Windows). */
export default function WidgetPage() {
  const [policy, setPolicy] = useState<WidgetPolicy | null>(null);
  const [uses, setUses] = useState<WidgetUse[] | null>(null);
  const [editing, setEditing] = useState(false);
  const [sites, setSites] = useState('');
  const [message, setMessage] = useState('');
  const [error, setError] = useState('');

  const load = () => Promise.all([api.widgetPolicy().then(setPolicy), api.widgetUses().then(setUses)]).catch(e => setError(e.message));
  useEffect(() => { load(); }, []);

  const save = async () => {
    setError(''); setMessage('');
    try {
      const p = await api.setWidgetBlocked(sites.split(/[\n,]+/).map(s => s.trim()).filter(Boolean));
      setPolicy(p); setEditing(false);
      setMessage(p.blockedSites.length ? `Saved. The Widget is switched off in ${p.blockedSites.length} website${p.blockedSites.length === 1 ? '' : 's'} or program${p.blockedSites.length === 1 ? '' : 's'}.` : 'Saved. The Widget works everywhere.');
    } catch (e: any) { setError(e.message); }
  };

  const when = (d: string) => new Date(d).toLocaleString('en-GB', { day: '2-digit', month: 'short', hour: '2-digit', minute: '2-digit' });
  const secs = (ms: number | null) => ms == null ? '' : `${(ms / 1000).toFixed(0)} s`;
  const admin = policy?.canChange === true;

  return (
    <>
      <div className="card">
        <h2>Widget</h2>
        <p className="note">Speak into any text box on any website (an email, a form, a chat) with the VerbaFlow extension for Microsoft Edge or Google Chrome.
          The words are not kept by VerbaFlow: only that the Widget was used, on which website, and for how long.</p>

        <h3>Install it (once per computer)</h3>
        <ol>
          <li>Open Edge and go to <code>edge://extensions</code> (in Chrome: <code>chrome://extensions</code>).</li>
          <li>Switch on <strong>Developer mode</strong>.</li>
          <li>Click <strong>Load unpacked</strong> and choose the folder <code>VerbaFlow\widget\extension</code>.</li>
          <li>The VerbaFlow Widget settings open. Click <strong>Allow the microphone</strong>, check the VerbaFlow address
            (<code>http://localhost:5044</code> on this computer), pick who you are and the language you speak, then <strong>Save and check</strong>.</li>
        </ol>

        <h3>Use it</h3>
        <ol>
          <li>Click in a text box. A small microphone button appears in its corner (or press <kbd>Alt</kbd>+<kbd>Shift</kbd>+<kbd>D</kbd>).</li>
          <li><strong>Red</strong>: it is listening, and the words appear as you speak. Click <strong>Stop</strong> when you are done.</li>
          <li><strong>Amber</strong>: check the words and change them if needed, then <strong>Accept</strong> (or <kbd>Ctrl</kbd>+<kbd>Enter</kbd>),{' '}
            <strong>Speak more</strong>, or <strong>Cancel</strong>.</li>
          <li><strong>Green</strong>: the words are in the box. A one-line box or plain text area is then locked; double-click it to change it.
            To replace part of what is there, select it first, then click the microphone.</li>
        </ol>
        <p className="note">The button never appears in password, card number or one-time code boxes. Spoken commands ("comma", "new paragraph") and saved text work as in Dictate. To correct by voice, say "scratch that" or "replace Tuesday with Thursday".</p>

        <h3>In Windows programs (Outlook, Word, Teams…)</h3>
        <p className="note">The Widget for Windows works in any program on a Windows computer, in the same way. Not yet tried on a real computer.</p>
        <ol>
          <li>In the VerbaFlow folder, double-click <code>widget-windows.cmd</code>. It installs the Widget, adds <strong>VerbaFlow Widget</strong> to the Start menu and starts it.</li>
          <li>In its settings, check the VerbaFlow address, click <strong>Save and check</strong>, then <strong>Sign in with Microsoft</strong>.</li>
          <li>Click where you want the words in any program, press <kbd>Alt</kbd>+<kbd>Shift</kbd>+<kbd>D</kbd> and speak; press it again to stop, check the words, then <strong>Accept</strong>.</li>
        </ol>
        <p className="note">Its icon is near the clock: click it for the settings. In Chrome and Edge the shortcut is left to the browser extension.</p>
      </div>

      <div className="card">
        <h2>Websites and programs where the Widget is switched off</h2>
        {error && <div className="error">{error}</div>}
        {message && <div className="ok" role="status">{message}</div>}
        {policy === null ? <p className="note">Loading…</p> : editing
          ? <div className="field">
            <label className="field">One per line: a website such as mail.example.com (it also covers its sub-sites), or a Windows program's file name such as teams.exe.
              <textarea rows={8} value={sites} onChange={e => setSites(e.target.value)} /></label>
            <div className="actions"><button className="primary" onClick={save}>Save</button><button onClick={() => setEditing(false)}>Cancel</button></div>
          </div>
          : <>
            {policy.blockedSites.length === 0
              ? <p className="note">None: the Widget works everywhere.</p>
              : <ul>{policy.blockedSites.map(s => <li key={s}>{s}</li>)}</ul>}
            {policy.changedBy && policy.changedAt && <p className="note">Last changed by {policy.changedBy}, {when(policy.changedAt)}.</p>}
            {admin
              ? <div className="actions"><button onClick={() => { setSites(policy.blockedSites.join('\n')); setEditing(true); setMessage(''); }}>Change the list</button></div>
              : <p className="note">Only administrators can change this list.</p>}
          </>}
      </div>

      <div className="card">
        <h2>{admin ? 'Recent uses (everyone)' : 'Your recent uses'}</h2>
        <div className="scroll">
          <table>
            <thead><tr><th>Started</th>{admin && <th>Who</th>}<th>Website or program</th><th>Browser or device</th><th>Language</th><th>Result</th><th>Spoken</th><th>Words</th></tr></thead>
            <tbody>
              {uses === null && <tr><td colSpan={8}>Loading…</td></tr>}
              {uses !== null && uses.length === 0 && <tr><td colSpan={8} className="note">The Widget has not been used yet.</td></tr>}
              {uses?.map(u => <tr key={u.id}>
                <td>{when(u.startedAt)}</td>{admin && <td>{u.user ?? ''}</td>}<td>{u.site}</td><td>{u.device}</td><td>{u.languages}</td>
                <td>{OUTCOMES[u.outcome] ?? u.outcome}{u.listens > 1 ? ` (${u.listens} listens)` : ''}</td><td>{secs(u.spokenMs)}</td><td>{u.words ?? ''}</td>
              </tr>)}
            </tbody>
          </table>
        </div>
      </div>
    </>
  );
}
