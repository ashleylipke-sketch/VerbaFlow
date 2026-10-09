import { useEffect, useRef, useState } from 'react';
import { api, fmtLen } from '../api';

type Marker = { type: 'Objection' | 'Pause' | 'Resume'; offsetMs: number; note: string | null };

export default function Record() {
  const [consent, setConsent] = useState(false);
  const [name, setName] = useState('');
  const [state, setState] = useState<'idle' | 'recording' | 'paused' | 'stopped'>('idle');
  const [elapsed, setElapsed] = useState(0);
  const [level, setLevel] = useState(0);
  const [markers, setMarkers] = useState<Marker[]>([]);
  const [pausing, setPausing] = useState(false);
  const [reason, setReason] = useState('');
  const [error, setError] = useState('');
  const [busy, setBusy] = useState(false);
  const [confirmDiscard, setConfirmDiscard] = useState(false);
  const [previewUrl, setPreviewUrl] = useState('');
  const rec = useRef<MediaRecorder | null>(null);
  const chunks = useRef<Blob[]>([]);
  const blob = useRef<Blob | null>(null);
  const stream = useRef<MediaStream | null>(null);
  const activeMs = useRef(0);        // recorded audio time, excluding pauses
  const lastTick = useRef(0);
  const audioCtx = useRef<AudioContext | null>(null);
  const raf = useRef(0);

  useEffect(() => () => { cancelAnimationFrame(raf.current); stream.current?.getTracks().forEach(t => t.stop()); audioCtx.current?.close(); }, []);

  useEffect(() => {
    if (state !== 'recording') return;
    lastTick.current = performance.now();
    const t = setInterval(() => { const n = performance.now(); activeMs.current += n - lastTick.current; lastTick.current = n; setElapsed(activeMs.current); }, 200);
    return () => { clearInterval(t); activeMs.current += performance.now() - lastTick.current; };
  }, [state]);

  const start = async () => {
    setError('');
    try {
      const s = await navigator.mediaDevices.getUserMedia({ audio: { channelCount: 1, echoCancellation: true, noiseSuppression: true, autoGainControl: true } });
      stream.current = s;
      const ctx = new AudioContext(); audioCtx.current = ctx;
      const an = ctx.createAnalyser(); an.fftSize = 512; ctx.createMediaStreamSource(s).connect(an);
      const buf = new Uint8Array(an.fftSize);
      const loop = () => { an.getByteTimeDomainData(buf); let peak = 0; for (const v of buf) peak = Math.max(peak, Math.abs(v - 128)); setLevel(Math.min(1, peak / 80)); raf.current = requestAnimationFrame(loop); };
      loop();
      const r = new MediaRecorder(s); rec.current = r; chunks.current = [];
      r.ondataavailable = e => e.data.size && chunks.current.push(e.data);
      r.onstop = () => { blob.current = new Blob(chunks.current, { type: r.mimeType || 'audio/webm' }); };
      r.start(1000); activeMs.current = 0; setElapsed(0); setMarkers([]); setState('recording');
    } catch (e: any) { setError(e?.name === 'NotAllowedError' ? 'VerbaFlow does not have permission to use the microphone. Allow it in your browser, then try again.' : 'The microphone could not be started. Check that one is connected and not in use by another app, then try again.'); }
  };

  const at = () => Math.round(activeMs.current + (state === 'recording' ? performance.now() - lastTick.current : 0));
  const objection = () => setMarkers(m => [...m, { type: 'Objection', offsetMs: at(), note: null }]);
  const confirmPause = () => {
    if (!reason.trim()) return;
    setMarkers(m => [...m, { type: 'Pause', offsetMs: at(), note: reason.trim() }]);
    rec.current?.pause(); setState('paused'); setPausing(false); setReason('');
  };
  const resume = () => { setMarkers(m => [...m, { type: 'Resume', offsetMs: at(), note: null }]); rec.current?.resume(); setState('recording'); };
  const stop = () => {
    rec.current?.stop(); stream.current?.getTracks().forEach(t => t.stop()); cancelAnimationFrame(raf.current); setLevel(0); setState('stopped');
  };

  // Once recording has stopped the audio exists only in this browser tab, so offer a preview and warn before it is lost.
  useEffect(() => {
    if (state !== 'stopped') return;
    const t = setTimeout(() => { if (blob.current) setPreviewUrl(URL.createObjectURL(blob.current)); }, 200);
    const warn = (e: BeforeUnloadEvent) => { e.preventDefault(); e.returnValue = ''; };
    window.addEventListener('beforeunload', warn);
    return () => { clearTimeout(t); window.removeEventListener('beforeunload', warn); };
  }, [state]);
  useEffect(() => () => { if (previewUrl) URL.revokeObjectURL(previewUrl); }, [previewUrl]);

  /** Throws the recording away without uploading anything, and returns to the start. */
  const discard = () => {
    blob.current = null; chunks.current = []; activeMs.current = 0;
    setPreviewUrl(''); setMarkers([]); setElapsed(0); setConfirmDiscard(false); setError(''); setState('idle');
  };

  const save = async () => {
    setBusy(true); setError('');
    try {
      await new Promise(r => setTimeout(r, 100));
      const f = new FormData();
      f.append('audio', blob.current!, 'recording.webm');
      f.append('name', name || `Meeting ${new Date().toLocaleString('en-GB')}`);
      f.append('lengthMs', String(Math.max(1, Math.round(activeMs.current))));
      f.append('language', 'en'); f.append('consentNoticeGiven', String(consent)); f.append('markers', JSON.stringify(markers));
      const { id } = await api.form('/items/recordings', f);
      location.hash = `#/items/${id}`;
    } catch (e: any) { setError(e.message); } finally { setBusy(false); }
  };

  return (
    <div className="card">
      <h2>Record a meeting</h2>
      {state === 'idle' && <>
        <div className="banner"><strong>Before you start:</strong> tell everyone present that the meeting is being recorded and transcribed.</div>
        <label className="row"><input type="checkbox" checked={consent} onChange={e => setConsent(e.target.checked)} />
          <span>I have told everyone present that this meeting is being recorded.</span></label>
        <div className="field"><label>Name<br /><input value={name} onChange={e => setName(e.target.value)} placeholder="e.g. Board meeting" /></label></div>
        <button className="primary" disabled={!consent} onClick={start}>Start recording</button>
      </>}
      {state !== 'idle' && <>
        <div className="timer" aria-live="off">{fmtLen(elapsed)} {state === 'paused' && <span className="tag warn">Paused</span>}</div>
        <div className="meter" aria-label="Microphone level"><div style={{ width: `${level * 100}%` }} /></div>
        <div className="actions" style={{ margin: '12px 0' }}>
          {state === 'recording' && <><button onClick={() => setPausing(true)} disabled={pausing}>Pause</button><button onClick={objection}>Log objection</button><button className="danger" onClick={stop}>Stop</button></>}
          {state === 'paused' && <><button className="primary" onClick={resume}>Resume</button><button className="danger" onClick={stop}>Stop</button></>}
        </div>
        {pausing && <div className="field"><label>Why are you pausing? (required, kept in the record)<br />
          <input autoFocus value={reason} onChange={e => setReason(e.target.value)} onKeyDown={e => e.key === 'Enter' && confirmPause()} /></label>
          <div className="actions"><button className="primary" disabled={!reason.trim()} onClick={confirmPause}>Pause recording</button><button onClick={() => setPausing(false)}>Cancel</button></div></div>}
        {markers.length > 0 && <ul className="note">{markers.map((m, i) => <li key={i}>{fmtLen(m.offsetMs)} — {m.type}{m.note ? `: ${m.note}` : ''}</li>)}</ul>}
        {state === 'stopped' && <>
          <p className="note">Recording stopped. It is not saved yet. Listen back if you like, then save it or discard it.</p>
          {previewUrl && <audio controls src={previewUrl} style={{ width: '100%', marginBottom: 12 }} />}
          {!confirmDiscard && <div className="actions">
            <button className="primary" disabled={busy} onClick={save}>{busy ? 'Saving…' : 'Save recording'}</button>
            <button className="danger" disabled={busy} onClick={() => setConfirmDiscard(true)}>Discard recording</button></div>}
          {confirmDiscard && <div className="banner"><strong>Discard this recording?</strong> Nothing has been saved, so it cannot be recovered.
            <div className="actions" style={{ marginTop: 8 }}><button className="danger" onClick={discard}>Yes, discard it</button><button onClick={() => setConfirmDiscard(false)}>No, keep it</button></div></div>}
        </>}
      </>}
      {error && <div className="error">{error}</div>}
    </div>
  );
}
