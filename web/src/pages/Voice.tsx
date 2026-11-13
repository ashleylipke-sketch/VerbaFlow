import { useEffect, useRef, useState } from 'react';
import { api } from '../api';
import { authHeaders } from '../auth';
import { decode, toWav } from '../takes';

type VoiceView = { available: boolean; saved: { savedAt: string; lengthMs: number } | null; peopleSaved: number; personal: boolean };

const MIN_S = 20;
const MAX_S = 90;

/** Something to read aloud: plain sentences with a mix of sounds. Any words will do. */
const PASSAGE = `When the sunlight strikes raindrops in the air, they act as a prism and form a rainbow. The rainbow is a
division of white light into many beautiful colours. These take the shape of a long round arch, with its path high above,
and its two ends apparently beyond the horizon. There is, according to legend, a boiling pot of gold at one end. People
look, but no one ever finds it. When a man looks for something beyond his reach, his friends say he is looking for the pot
of gold at the end of the rainbow.`;

/**
 * Your voice: a person records about half a minute of speech so VerbaFlow can recognise them in meetings recorded in a
 * room, without introductions. Only they can save, hear or remove it.
 */
export default function VoicePage() {
  const [view, setView] = useState<VoiceView | null>(null);
  const [error, setError] = useState('');
  const [agreed, setAgreed] = useState(false);
  const [recording, setRecording] = useState(false);
  const [seconds, setSeconds] = useState(0);
  const [take, setTake] = useState<Blob | null>(null);
  const [takeUrl, setTakeUrl] = useState<string | null>(null);
  const [savedUrl, setSavedUrl] = useState<string | null>(null);
  const [busy, setBusy] = useState('');
  const [again, setAgain] = useState(false);
  const rec = useRef<MediaRecorder | null>(null);
  const stream = useRef<MediaStream | null>(null);
  const timer = useRef<number | null>(null);
  const started = useRef(0);

  const load = () => api.get<VoiceView>('/voice').then(setView).catch(e => setError(e.message));
  useEffect(() => { load(); return () => stop(true); }, []);
  useEffect(() => () => { if (takeUrl) URL.revokeObjectURL(takeUrl); }, [takeUrl]);
  useEffect(() => () => { if (savedUrl) URL.revokeObjectURL(savedUrl); }, [savedUrl]);

  async function start() {
    setError(''); setTake(null); setTakeUrl(null);
    try {
      const s = await navigator.mediaDevices.getUserMedia({ audio: { channelCount: 1, echoCancellation: false, noiseSuppression: false, autoGainControl: false } });
      stream.current = s;
      const r = new MediaRecorder(s); rec.current = r;
      const chunks: Blob[] = [];
      r.ondataavailable = e => { if (e.data.size) chunks.push(e.data); };
      r.onstop = async () => {
        s.getTracks().forEach(t => t.stop());
        const length = (Date.now() - started.current) / 1000;
        if (length < MIN_S) { setError(`That was ${Math.round(length)} seconds. Keep reading for at least ${MIN_S} seconds, then press Stop.`); return; }
        try {
          const wav = toWav(await decode(new Blob(chunks, { type: r.mimeType })));
          setTake(wav); setTakeUrl(URL.createObjectURL(wav));
        } catch { setError('The recording could not be read. Try again, or use Chrome or Edge.'); }
      };
      started.current = Date.now(); setSeconds(0); setRecording(true);
      r.start();
      timer.current = window.setInterval(() => {
        const s2 = Math.floor((Date.now() - started.current) / 1000);
        setSeconds(s2);
        if (s2 >= MAX_S) stop();
      }, 250);
    } catch { setError('VerbaFlow may not use the microphone. Allow it in the browser (the icon at the left of the address bar) and try again.'); }
  }

  function stop(quiet = false) {
    if (timer.current) { clearInterval(timer.current); timer.current = null; }
    setRecording(false);
    if (rec.current && rec.current.state !== 'inactive') {
      if (quiet) rec.current.onstop = () => stream.current?.getTracks().forEach(t => t.stop());
      rec.current.stop();
    }
  }

  async function save() {
    if (!take) return;
    setBusy('Saving your voice…'); setError('');
    const f = new FormData();
    f.append('audio', take, 'voice.wav');
    f.append('agreed', agreed ? 'true' : 'false');
    try { await api.form('/voice', f); setTake(null); setTakeUrl(null); setAgain(false); setSavedUrl(null); await load(); }
    catch (e) { setError((e as Error).message); }
    finally { setBusy(''); }
  }

  async function remove() {
    if (!confirm('Remove your saved voice? Meetings will no longer be named from it.')) return;
    setBusy('Removing…'); setError('');
    try { await api.del('/voice'); setSavedUrl(null); setAgain(false); await load(); }
    catch (e) { setError((e as Error).message); }
    finally { setBusy(''); }
  }

  async function listen() {
    try {
      const res = await fetch('/api/voice/audio', { headers: await authHeaders() });
      if (!res.ok) throw new Error();
      setSavedUrl(URL.createObjectURL(await res.blob()));
    } catch { setError('Your saved recording could not be loaded.'); }
  }

  if (!view) return <div className="card"><h2>Your voice</h2>{error ? <div className="error">{error}</div> : <p className="note">Loading…</p>}</div>;
  const recorder = view.available && (!view.saved || again);
  return (
    <div className="card voice-page" style={{ maxWidth: 720 }}>
      <h2>Your voice</h2>
      <p>Save a short recording of your voice and VerbaFlow will try to recognise you in meetings recorded in a room
        {view.personal ? '' : ' by anyone in your organisation'}, and put your name on what you said instead of "Speaker 1".
        It is used only when a meeting has no introductions and does not come from Teams, Zoom or another online platform (those already give names).</p>
      {!view.available && <div className="error">Voices cannot be saved on this VerbaFlow because its voice model is switched off.</div>}
      {view.saved && <div className="voice-saved">
        <p><strong>Your voice is saved</strong> ({Math.round(view.saved.lengthMs / 1000)} seconds, {new Date(view.saved.savedAt).toLocaleDateString()}).</p>
        <p className="row">
          {savedUrl ? <audio controls src={savedUrl} /> : <button type="button" onClick={listen}>Listen to it</button>}
          {!again && view.available && <button type="button" onClick={() => { setAgain(true); setAgreed(true); }}>Record it again</button>}
          <button type="button" className="danger" onClick={remove} disabled={!!busy}>Remove my voice</button>
        </p>
      </div>}
      {recorder && <>
        <h3>{view.saved ? 'Record your voice again' : 'Record your voice'}</h3>
        <p className="note">Find a quiet place and use the microphone you use for meetings. Press Start and read the text below in your normal
          voice (any language is fine, or just talk about your day). Keep going for at least {MIN_S} seconds; it stops by itself after {MAX_S}.</p>
        <blockquote className="passage">{PASSAGE}</blockquote>
        <p className="row">
          {!recording ? <button type="button" className="primary" onClick={start} disabled={!!busy}>{take ? 'Start again' : 'Start'}</button>
            : <button type="button" className="primary" onClick={() => stop()}>Stop</button>}
          {recording && <span className={seconds < MIN_S ? 'note' : ''}>Recording: {seconds} s{seconds < MIN_S ? ` (at least ${MIN_S})` : ', you can stop now'}</span>}
          {again && !recording && <button type="button" onClick={() => { setAgain(false); setTake(null); setTakeUrl(null); }}>Cancel</button>}
        </p>
        {takeUrl && <>
          <p className="row"><audio controls src={takeUrl} /></p>
          <label className="agree"><input type="checkbox" checked={agreed} onChange={e => setAgreed(e.target.checked)} /> I agree that VerbaFlow keeps this
            recording and a fingerprint of my voice to recognise me in {view.personal ? 'my' : 'my organisation’s'} meetings. I can remove them here at any time.</label>
          <p className="row"><button type="button" className="primary" onClick={save} disabled={!agreed || !!busy}>Save my voice</button></p>
        </>}
      </>}
      {busy && <p className="note">{busy}</p>}
      {error && <div className="error">{error}</div>}
      <p className="note">{view.peopleSaved === 1 ? '1 person has' : `${view.peopleSaved} people have`} saved {view.peopleSaved === 1 ? 'their' : 'their'} voice{view.personal ? '' : ' in your organisation'}.
        The more people in a meeting have saved theirs, the more speakers can be named. A meeting recorded before someone saved their voice is named from it
        when it is run again (Speakers look wrong? on the meeting's page). Voice recognition can be wrong, especially with one microphone in a busy room,
        so check the names and fix them with Rename or Change speaker.</p>
    </div>
  );
}
