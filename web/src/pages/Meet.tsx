import { useCallback, useEffect, useState } from 'react';
import { api, type CalendarMeetingRow, type CalendarOverview, type MeetOverview, type NoteTakerRow } from '../api';
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

  const [checked, setChecked] = useState('');
  const check = async () => {
    setError(''); setChecked('Checking…');
    try { setO(await api.checkNoteTakers()); setChecked(`Checked at ${new Date().toLocaleTimeString('en-GB', { hour: '2-digit', minute: '2-digit', second: '2-digit' })}.`); }
    catch (e: any) { setChecked(''); setError(e.message); }
  };

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

      <CalendarCard onChanged={load} />

      <div className="card">
        <div className="item-head"><h2>Your note-takers</h2><span className="row">{checked && <span className="note" role="status">{checked}</span>}<button onClick={check}>Check now</button></span></div>
        {!o ? <p className="note">Loading…</p> : o.noteTakers.length === 0 ? <p className="note">None yet.</p> :
          <table>
            <thead><tr><th>Meeting</th><th>Platform</th><th>When</th><th>Status</th><th>Booked by</th><th></th></tr></thead>
            <tbody>{o.noteTakers.map(n => <tr key={n.id}>
              <td>{n.name}</td><td>{n.platform}</td><td>{when(n.joinAt)}</td>
              <td><span className="pill" title={n.failureReason ?? undefined}>{n.stateLabel}</span>
                {n.failureReason && <div className="note">{n.failureReason}</div>}
                {n.waiting && <div className="note">{n.waiting}</div>}
                {n.checkedAt && ACTIVE.has(n.state) && <div className="note">Last checked {new Date(n.checkedAt).toLocaleTimeString('en-GB', { hour: '2-digit', minute: '2-digit' })}</div>}</td>
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

/**
 * Your calendar: once connected, the note-taker goes to the online meetings in it by itself, an hour before each starts.
 * Each meeting can be switched on or off.
 */
function CalendarCard({ onChanged }: { onChanged: () => void }) {
  const [c, setC] = useState<CalendarOverview | null>(null);
  const [source, setSource] = useState('');
  const [link, setLink] = useState('');
  const [auto, setAuto] = useState(true);
  const [spoken, setSpoken] = useState('en-GB');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState('');

  const show = useCallback((o: CalendarOverview) => {
    setC(o);
    if (o.connected) { setAuto(o.connected.autoSend); setSpoken(o.connected.spokenLanguages); }
    else setSource(s => s && o.sources.some(x => x.id === s) ? s : o.sources[0]?.id ?? '');
  }, []);
  const load = useCallback(() => api.calendar().then(show).catch(e => setError(e.message)), [show]);
  useEffect(() => { load(); }, [load]);
  // While a calendar is connected, the list keeps itself up to date.
  useEffect(() => { if (!c?.connected) return; const i = setInterval(load, 60_000); return () => clearInterval(i); }, [c?.connected, load]);

  const run = async (f: () => Promise<CalendarOverview>) => {
    setBusy(true); setError('');
    try { show(await f()); onChanged(); } catch (e: any) { setError(e.message); } finally { setBusy(false); }
  };
  const connect = () => run(() => api.connectCalendar({ source, link: source === 'link' ? link.trim() : null, autoSend: auto, spokenLanguages: spoken }));
  const change = (b: { autoSend?: boolean; spokenLanguages?: string }) => run(() => api.connectCalendar(b));
  const disconnect = () => { if (confirm('Disconnect your calendar? Note-takers it booked for meetings that have not started are called off for you.')) run(() => api.disconnectCalendar()); };
  const flip = (m: CalendarMeetingRow) => run(() => api.switchCalendarMeeting(m.eventKey, !m.send));

  if (!c) return <div className="card"><h2>Your calendar</h2><p className="note">{error || 'Loading…'}</p></div>;
  const k = c.connected;
  return (
    <div className="card">
      <div className="item-head"><h2>Your calendar</h2>
        {k && <span><button disabled={busy} onClick={() => change({})}>Check my calendar now</button> <button disabled={busy} onClick={disconnect}>Disconnect</button></span>}</div>
      {!k ? <>
        <p className="note">Connect your calendar and the note-taker goes to the online meetings in it by itself, {c.bookAheadMinutes} minutes
          before each one starts. You can switch any meeting off, and meetings you decline are left out. If colleagues have the same meeting
          in their calendars, still only one note-taker joins.</p>
        {c.sources.length === 0 ? <p className="note">No calendar can be connected here yet.</p> : <>
          <div className="field"><label>Which calendar?</label>
            {c.sources.map(o => <label key={o.id} className="row"><input type="radio" name="calsource" checked={source === o.id} onChange={() => setSource(o.id)} /> <span>{o.label}</span></label>)}</div>
          {source === 'link' && <div className="field"><label>Your calendar's private address<br />
            <input value={link} onChange={e => setLink(e.target.value)} style={{ width: '100%' }} autoComplete="off" placeholder="https://… or webcal://…" /></label>
            <div className="note">Outlook.com: Settings, Calendar, Shared calendars, Publish a calendar, then copy the ICS link.
              Google Calendar: Settings, click your calendar, Integrate calendar, then copy the Secret address in iCal format.
              This address works like a password: VerbaFlow keeps it to itself and never shows it in full again.</div></div>}
          {source === 'sample' && <div className="banner">Stand-in: a made-up calendar with a few online meetings (the first starts about 25 minutes
            after you connect), so you can try this without Microsoft 365. Nobody joins any real meeting.</div>}
          <label className="row"><input type="checkbox" checked={auto} onChange={e => setAuto(e.target.checked)} />
            <span>Send the note-taker to all my online meetings (untick to choose meeting by meeting)</span></label>
          <LanguagePicker value={spoken} onChange={setSpoken} />
          {error && <div className="error">{error}</div>}
          <button className="primary" disabled={busy || !source || (source === 'link' && !link.trim())} onClick={connect}>{busy ? 'Connecting…' : 'Connect calendar'}</button>
        </>}
      </> : <>
        <p className="note">Connected: {k.sourceLabel}{k.shown && ` (${k.shown})`}.{k.lastReadAt && ` Last read ${when(k.lastReadAt)}.`} The note-taker is
          booked {c.bookAheadMinutes} minutes before a meeting starts.</p>
        {k.problem && <div className="error">{k.problem}</div>}
        {error && <div className="error">{error}</div>}
        <label className="row"><input type="checkbox" checked={auto} disabled={busy} onChange={e => change({ autoSend: e.target.checked })} />
          <span>Send the note-taker to all my online meetings (untick to choose meeting by meeting)</span></label>
        <LanguagePicker value={spoken} onChange={v => { setSpoken(v); change({ spokenLanguages: v }); }} />
        {c.meetings.length === 0 ? <p className="note">No online meetings in your calendar in the next 7 days.</p> :
          <table>
            <thead><tr><th>When</th><th>Meeting</th><th>Platform</th><th>Note-taker</th><th>Status</th></tr></thead>
            <tbody>{c.meetings.map(m => <tr key={m.eventKey}>
              <td>{when(m.start)}</td><td>{m.subject}</td><td>{m.platform}</td>
              <td><label className="row"><input type="checkbox" checked={m.send} disabled={busy} onChange={() => flip(m)}
                aria-label={`Send the note-taker to ${m.subject}`} /> <span>{m.send ? 'On' : 'Off'}</span></label></td>
              <td>{m.noteTakerLabel ?? m.why ?? (m.send ? `Will be booked ${c.bookAheadMinutes} minutes before it starts` : "")}</td>
            </tr>)}</tbody>
          </table>}
        {c.otherMeetings > 0 && <p className="note">{c.otherMeetings} other {c.otherMeetings === 1 ? 'meeting has' : 'meetings have'} no Zoom, Google Meet,
          Teams or Webex link, so the note-taker cannot join {c.otherMeetings === 1 ? 'it' : 'them'}.</p>}
      </>}
    </div>
  );
}

/** Someone else asked for this note-taker too, so calling it off only takes this person off. */
const shared = (n: NoteTakerRow) => n.youAsked && n.alsoFor.length > 0;

const when = (d: string) => new Date(d).toLocaleString('en-GB', { day: '2-digit', month: 'short', year: 'numeric', hour: '2-digit', minute: '2-digit' });
