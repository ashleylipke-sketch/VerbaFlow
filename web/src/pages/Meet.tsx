import { useCallback, useEffect, useState } from 'react';
import { api, type MeetOverview, type NoteTakerRow } from '../api';
import LanguagePicker from '../LanguagePicker';

const ACTIVE = new Set(['Scheduled', 'Joining', 'WaitingToBeLetIn', 'Recording', 'MeetingEnded']);

/** A date and time for a datetime-local box, in the computer's own time. */
function localInput(d: Date) {
  const p = (n: number) => String(n).padStart(2, '0');
  return `${d.getFullYear()}-${p(d.getMonth() + 1)}-${p(d.getDate())}T${p(d.getHours())}:${p(d.getMinutes())}`;
}

/** Meet: send a note-taker to an online meeting (Zoom, Google Meet, Teams, Webex); the recording comes back as a Meeting item. */
export default function Meet() {
  const [o, setO] = useState<MeetOverview | null>(null);
  const [link, setLink] = useState('');
  const [name, setName] = useState('');
  const [later, setLater] = useState(false);
  const [at, setAt] = useState(() => localInput(new Date(Date.now() + 60 * 60 * 1000)));
  const [spoken, setSpoken] = useState('en-GB');
  const [busy, setBusy] = useState(false);
  const [message, setMessage] = useState('');
  const [error, setError] = useState('');

  const load = useCallback(() => api.meet().then(setO).catch(e => setError(e.message)), []);
  useEffect(() => { load(); }, [load]);
  // While a note-taker is on its way or in a meeting, the list keeps itself up to date.
  const anyActive = o?.noteTakers.some(n => ACTIVE.has(n.state)) ?? false;
  useEffect(() => { if (!anyActive) return; const i = setInterval(load, 20_000); return () => clearInterval(i); }, [anyActive, load]);

  const book = async () => {
    setBusy(true); setError(''); setMessage('');
    try {
      const r = await api.bookNoteTaker({ meetingUrl: link.trim(), name: name.trim() || null, joinAt: later ? new Date(at).toISOString() : null, spokenLanguages: spoken });
      setMessage(r.alreadyBooked ? 'You had already booked the note-taker for this meeting.'
        : r.joinedExisting ? `${r.noteTaker.bookedBy} had already booked the note-taker for this meeting, so you have been added to that booking. Only one note-taker joins, and you will both get the recording.`
        : later ? `Booked. The note-taker will join at ${when(r.noteTaker.joinAt)}.` : 'The note-taker is on its way into the meeting.');
      setLink(''); setName('');
      await load();
    } catch (e: any) { setError(e.message); } finally { setBusy(false); }
  };

  const cancel = async (n: NoteTakerRow) => {
    const q = shared(n) ? 'Take yourself off this booking? The note-taker still joins for your colleagues.' : 'Call the note-taker off? If it is already in the meeting, it leaves and nothing is kept.';
    if (!confirm(q)) return;
    setError(''); setMessage('');
    try { await api.cancelNoteTaker(n.id); await load(); } catch (e: any) { setError(e.message); }
  };

  const check = async () => { setError(''); try { setO(await api.checkNoteTakers()); } catch (e: any) { setError(e.message); } };

  return (
    <>
      <div className="card">
        <h2>Meet: record an online meeting</h2>
        <p className="note">Paste the join link of a Zoom, Google Meet, Microsoft Teams or Webex meeting. A participant
          called <strong>{o?.noteTakerName ?? 'VerbaFlow Notetaker'}</strong> joins, records the sound and the video, and posts a note in the
          meeting chat saying it is recording. Nobody has to press record, and it works for meetings set up by other organisations:
          if the meeting has a lobby, the host needs to let it in. When the meeting ends, the recording comes back as a Meeting item,
          with speakers named as they were in the meeting.</p>
        <p className="note">Only one note-taker joins a meeting. If a colleague has already booked it, you are added to their booking:
          the first person to book owns the meeting item, and the others can read it and export it once it is approved.</p>
        {o && !o.real && <div className="banner">Stand-in: no note-taker joins any meeting yet. A short sample recording comes back
          instead, so everything else can be tried. The real note-taker needs a Recall.ai account (see docs/meet-setup.md).</div>}
        <div className="field"><label>Meeting link<br /><input value={link} onChange={e => setLink(e.target.value)} style={{ width: '100%' }}
          placeholder="https://zoom.us/j/… or https://meet.google.com/… or https://teams.microsoft.com/…" /></label></div>
        <div className="field"><label>Name (optional)<br /><input value={name} onChange={e => setName(e.target.value)} placeholder="e.g. Board meeting" /></label></div>
        <div className="field"><label>When should it join?</label>
          <label className="row"><input type="radio" checked={!later} onChange={() => setLater(false)} /> <span>Now</span></label>
          <label className="row"><input type="radio" checked={later} onChange={() => setLater(true)} /> <span>At</span>
            <input type="datetime-local" value={at} onChange={e => { setAt(e.target.value); setLater(true); }} /></label>
          <div className="note">Book it ahead when you can: a note-taker sent at the last minute can occasionally be refused when the service is busy.</div></div>
        <LanguagePicker value={spoken} onChange={setSpoken} />
        {error && <div className="error">{error}</div>}
        {message && <div className="banner">{message}</div>}
        <button className="primary" disabled={busy || !link.trim()} onClick={book}>{busy ? 'Booking…' : 'Send the note-taker'}</button>
      </div>

      <div className="card">
        <div className="item-head"><h2>Your note-takers</h2><button onClick={check}>Check now</button></div>
        {!o ? <p className="note">Loading…</p> : o.noteTakers.length === 0 ? <p className="note">None yet.</p> :
          <table>
            <thead><tr><th>Meeting</th><th>Platform</th><th>When</th><th>Status</th><th>Booked by</th><th></th></tr></thead>
            <tbody>{o.noteTakers.map(n => <tr key={n.id}>
              <td>{n.name}</td><td>{n.platform}</td><td>{when(n.joinAt)}</td>
              <td><span className="pill" title={n.failureReason ?? undefined}>{n.stateLabel}</span>
                {n.failureReason && <div className="note">{n.failureReason}</div>}</td>
              <td>{n.bookedBy}{n.alsoFor.length > 0 && <div className="note">also for {n.alsoFor.join(', ')}</div>}</td>
              <td>{n.itemId && <a className="btn" href={`#/items/${n.itemId}`}>Open the meeting</a>}
                {n.canCancel && <button onClick={() => cancel(n)}>{shared(n) ? 'Take me off' : 'Call off'}</button>}</td>
            </tr>)}</tbody>
          </table>}
        <p className="note">The list checks itself every 20 seconds while a note-taker is on its way or in a meeting.</p>
      </div>
    </>
  );
}

/** Someone else asked for this note-taker too, so calling it off only takes this person off. */
const shared = (n: NoteTakerRow) => n.youAsked && n.alsoFor.length > 0;

const when = (d: string) => new Date(d).toLocaleString('en-GB', { day: '2-digit', month: 'short', year: 'numeric', hour: '2-digit', minute: '2-digit' });
