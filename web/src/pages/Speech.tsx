import { useEffect, useMemo, useRef, useState } from 'react';
import { keepAwake, lostMessage } from '../keepAwake';
import { api, fmtLen } from '../api';
import LanguagePicker from '../LanguagePicker';
import DocumentTypePicker, { docLabel, type DocType } from '../DocumentType';
import { summaryLanguage } from '../languages';
import { RATE, decode, msOf, samplesAt, toWav } from '../takes';
import { Downsampler, TAP, from, join, median, retryDelay, toPcm16 } from '../live';
import { paragraphMarkers, paragraphPieces } from '../paragraphs';
import { CommandHelp, drawWave } from './Record';

type Phase = 'loading' | 'setup' | 'starting' | 'live' | 'paused' | 'finishing' | 'review' | 'saving';
/** The connection to the live speech service: being made, working, lost (being made again), stopped for good, or finished. */
type Link = 'connecting' | 'live' | 'lost' | 'off' | 'done';
export type LiveSeg = { text: string; newParagraph: boolean | null; list?: 'bullet' | 'number' | null };

/**
 * Speech mode: the text is typed as the person speaks. The microphone goes to the live speech service and is kept,
 * untouched apart from being made 16 kHz mono, as the recording. If the connection drops, recording carries on and the
 * text catches up when it is back; if it never comes back, the text is worked out from the recording on saving.
 * It can be saved as a Draft and carried on later (draftId): the saved recording and text come back and new speech is added at the end.
 */
export default function Speech({ draftId }: { draftId?: string }) {
  const [name, setName] = useState('');
  const [clientRef, setClientRef] = useState('');
  const [docType, setDocType] = useState<DocType | ''>('');
  const [spoken, setSpoken] = useState('en-GB');
  const [phase, setPhase] = useState<Phase>(draftId ? 'loading' : 'setup');
  const [id, setId] = useState<string | null>(draftId ?? null);
  const [dirty, setDirty] = useState(false);
  const [link, setLink] = useState<Link>('connecting');
  const [segs, setSegs] = useState<LiveSeg[]>([]);
  const [pending, setPending] = useState(0);
  const [elapsed, setElapsed] = useState(0);
  const [delay, setDelay] = useState<number | null>(null);
  const [quiet, setQuiet] = useState(false);
  const [error, setError] = useState('');
  const [notice, setNotice] = useState('');
  const [lost, setLost] = useState('');
  const awake = useRef<(() => void) | null>(null);
  const [confirmDiscard, setConfirmDiscard] = useState(false);
  const [message, setMessage] = useState('');

  const session = useRef<string | null>(null);
  const ws = useRef<WebSocket | null>(null);
  const ready = useRef(false);
  const chunks = useRef<Float32Array[]>([]);
  const samples = useRef(0);
  const finalTo = useRef(0);       // where the finished text ends: a new connection sends the audio again from here
  const catchUpTo = useRef(0);     // text up to here is catching up after a lost connection, so it says nothing about delay
  const stopping = useRef(false);
  const finished = useRef(false);
  const liveOff = useRef(false);
  const attempt = useRef(0);
  const retryTimer = useRef(0);
  const finishTimer = useRef(0);
  const delays = useRef<number[]>([]);
  const pendingTicket = useRef<string | undefined>(undefined);
  const paused = useRef(false);
  const stream = useRef<MediaStream | null>(null);
  const audioCtx = useRef<AudioContext | null>(null);
  const raf = useRef(0);
  const wave = useRef<HTMLCanvasElement>(null);

  const stopMic = () => {
    awake.current?.(); awake.current = null;
    cancelAnimationFrame(raf.current);
    stream.current?.getTracks().forEach(t => t.stop()); stream.current = null;
    audioCtx.current?.close().catch(() => {}); audioCtx.current = null;
  };
  const closeLink = () => {
    clearTimeout(retryTimer.current); clearTimeout(finishTimer.current);
    const s = ws.current; ws.current = null; ready.current = false;
    if (s && s.readyState <= 1) s.close();
  };
  useEffect(() => () => { stopMic(); closeLink(); }, []);

  // Until it is saved, the recording exists only in this browser tab, so the browser asks before it is lost.
  const unsaved = phase !== 'setup' && phase !== 'saving' && phase !== 'loading' && (dirty || !id);
  useEffect(() => {
    if (!unsaved) return;
    const warn = (e: BeforeUnloadEvent) => { e.preventDefault(); e.returnValue = ''; };
    window.addEventListener('beforeunload', warn);
    return () => window.removeEventListener('beforeunload', warn);
  }, [unsaved]);

  useEffect(() => {
    if (phase !== 'live' && phase !== 'paused') return;
    const t = setInterval(() => setElapsed(msOf(samples.current)), 200);
    return () => clearInterval(t);
  }, [phase]);

  // Carrying on with a Draft: its details, its newest saved recording and the text heard before.
  useEffect(() => {
    if (!draftId) return;
    (async () => {
      try {
        const d = await api.item(draftId);
        if (d.row.statusCode !== 'Draft') { location.hash = `#/items/${draftId}`; return; }
        if (!d.row.actions.includes('continue')) { setError('Only the person who spoke this can carry on with it.'); setPhase('setup'); return; }
        setName(d.row.name); setClientRef(d.clientReference ?? ''); setDocType((d.documentType ?? 'document') as DocType); setSpoken(d.spokenLanguages);
        const audio = await decode(await api.audio(draftId));
        chunks.current = [audio]; samples.current = audio.length; setElapsed(msOf(audio.length));
        stopping.current = true; finished.current = true;
        try {
          const r = await api.post('/live', { carryOn: draftId });
          session.current = r.id; pendingTicket.current = r.ticket;
          setSegs(r.segments ?? []); finalTo.current = r.finalToMs ?? 0;
        } catch (e: any) { liveOff.current = true; setLink('off'); setNotice(e?.message || 'The live text could not be started.'); }
        setPhase('review');
      } catch (e: any) { setError(e?.message || 'This could not be opened. Try again.'); setPhase('setup'); }
    })();
  }, [draftId]);

  const retry = () => {
    clearTimeout(retryTimer.current);
    if (!session.current || finished.current || liveOff.current) return;
    retryTimer.current = window.setTimeout(() => connect(), retryDelay(attempt.current++));
  };

  const connect = async (firstTicket?: string) => {
    if (!session.current || finished.current || liveOff.current) return;
    ready.current = false;
    let ticket = firstTicket;
    try { ticket ??= (await api.post(`/live/${session.current}/ticket`)).ticket as string; }
    catch { setLink('lost'); retry(); return; }
    const fromMs = finalTo.current;
    const sock = new WebSocket(`${location.protocol === 'https:' ? 'wss' : 'ws'}://${location.host}/api/live/${session.current}/socket?ticket=${ticket}&fromMs=${fromMs}`);
    sock.binaryType = 'arraybuffer';
    ws.current = sock;
    sock.onmessage = ev => {
      if (ws.current !== sock) return;
      const m = JSON.parse(ev.data);
      if (m.type === 'ready') {
        attempt.current = 0;
        setLink('live');
        if (Array.isArray(m.segments)) { setSegs(m.segments); setPending(0); }
        // Everything recorded since the finished text ends goes (again), so nothing said while disconnected is missed.
        const back = from(chunks.current, samplesAt(fromMs));
        for (let i = 0; i < back.length; i += RATE) sock.send(toPcm16(back.subarray(i, i + RATE)));
        catchUpTo.current = msOf(samples.current);
        ready.current = true;
        if (stopping.current) sock.send(JSON.stringify({ type: 'end' }));
      } else if (m.type === 'text') {
        setSegs(m.segments); setPending(m.pendingWords ?? 0);
        finalTo.current = m.finalToMs ?? finalTo.current;
        // How far the text is behind the voice: the audio recorded so far, less the point the text has reached.
        if (typeof m.heardToMs === 'number' && !stopping.current && m.heardToMs > catchUpTo.current) {
          const d = msOf(samples.current) - m.heardToMs;
          if (d >= 0 && d < 60_000) { delays.current.push(d); setDelay(median(delays.current.slice(-8))); }
        }
      } else if (m.type === 'done') {
        finished.current = true;
        setSegs(m.segments); setPending(0); finalTo.current = m.finalToMs ?? finalTo.current;
        setLink('done');
        if (stopping.current) setPhase('review');
      } else if (m.type === 'error') {
        liveOff.current = true;
        setLink('off');
        setNotice(m.message);
        if (stopping.current) setPhase('review');
      }
    };
    sock.onclose = () => {
      if (ws.current !== sock) return;
      ws.current = null; ready.current = false;
      if (!finished.current && !liveOff.current) { setLink('lost'); retry(); }
    };
  };

  /** Starts the microphone; what it hears is added to the end of the recording. False if it could not be started. */
  const openMic = async (back: Phase) => {
    try {
      const s = await navigator.mediaDevices.getUserMedia({ audio: { channelCount: 1, echoCancellation: false, noiseSuppression: false, autoGainControl: false } });
      stream.current = s;
      const ctx = new AudioContext(); audioCtx.current = ctx;
      await ctx.resume().catch(() => {}); // iPhones start the audio paused
      setLost('');
      awake.current = keepAwake(s, why => { setLost(lostMessage(why)); if (!stopping.current) stop(); });
      const url = URL.createObjectURL(new Blob([TAP], { type: 'application/javascript' }));
      await ctx.audioWorklet.addModule(url);
      URL.revokeObjectURL(url);
      const src = ctx.createMediaStreamSource(s);
      const tap = new AudioWorkletNode(ctx, 'vf-tap');
      const silent = ctx.createGain(); silent.gain.value = 0; // keeps the tap running without playing the microphone back
      src.connect(tap).connect(silent).connect(ctx.destination);
      const down = new Downsampler(ctx.sampleRate);
      tap.port.onmessage = e => {
        if (paused.current || stopping.current) return;
        const x = down.push(e.data as Float32Array);
        if (!x.length) return;
        chunks.current.push(x); samples.current += x.length;
        if (ready.current && ws.current?.readyState === WebSocket.OPEN) ws.current.send(toPcm16(x));
      };
      const an = ctx.createAnalyser(); an.fftSize = 512; src.connect(an);
      const buf = new Uint8Array(an.fftSize);
      const calm = window.matchMedia('(prefers-reduced-motion: reduce)').matches;
      const bars: number[] = []; let lastBar = 0, lastHeard = performance.now(), isQuiet = false;
      const loop = (now = performance.now()) => {
        raf.current = requestAnimationFrame(loop);
        if (now - lastBar < (calm ? 150 : 40)) return;
        lastBar = now;
        an.getByteTimeDomainData(buf);
        let peak = 0; for (const v of buf) peak = Math.max(peak, Math.abs(v - 128));
        const level = paused.current ? 0 : Math.min(1, peak / 64);
        if (level > 0.04) lastHeard = now;
        const nowQuiet = !paused.current && now - lastHeard > 4000;
        if (nowQuiet !== isQuiet) { isQuiet = nowQuiet; setQuiet(nowQuiet); }
        drawWave(wave.current, bars, level);
      };
      loop();
    } catch (e: any) {
      stopMic(); setPhase(back);
      setError(e?.name === 'NotAllowedError' ? 'VerbaFlow does not have permission to use the microphone. Allow it in your browser, then try again.' : 'The microphone could not be started. Check that one is connected and not in use by another app, then try again.');
      return false;
    }
    setPhase('live'); setDirty(true);
    return true;
  };

  const start = async () => {
    setError(''); setNotice(''); setMessage(''); setPhase('starting');
    chunks.current = []; samples.current = 0; finalTo.current = 0; catchUpTo.current = 0; delays.current = []; attempt.current = 0;
    stopping.current = false; finished.current = false; liveOff.current = false; paused.current = false;
    setSegs([]); setPending(0); setDelay(null); setElapsed(0); setLink('connecting');
    if (!await openMic('setup')) return;
    // Recording has started. The text follows once the live connection is made; anything said before then is sent when it is.
    try {
      const r = await api.post('/live', { spokenLanguages: spoken });
      session.current = r.id;
      connect(r.ticket);
    } catch (e: any) {
      liveOff.current = true; setLink('off');
      setNotice(e?.message || 'The live text could not be started.');
    }
  };

  // Speaking again after Stop, or after carrying on with a Draft: the new speech goes at the end, and the live text
  // carries on from where the finished text ends (anything after that is sent again, so nothing is missed).
  const speakMore = async () => {
    setError(''); setMessage(''); setPhase('starting');
    stopping.current = false; finished.current = false; paused.current = false; attempt.current = 0;
    catchUpTo.current = msOf(samples.current);
    if (!await openMic('review')) { stopping.current = true; finished.current = true; return; }
    if (liveOff.current || !session.current) return;
    setLink('connecting');
    try { if (!pendingTicket.current) await api.post(`/live/${session.current}/resume`); }
    catch { liveOff.current = true; setLink('off'); setNotice('The live text has stopped.'); return; }
    const t = pendingTicket.current; pendingTicket.current = undefined;
    connect(t);
  };

  const pause = () => { paused.current = true; setPhase('paused'); };
  const resume = () => { paused.current = false; setPhase('live'); };

  const stop = () => {
    stopping.current = true; paused.current = false;
    stopMic(); setQuiet(false); setElapsed(msOf(samples.current));
    if (finished.current || liveOff.current || !session.current) { setPhase('review'); return; }
    setPhase('finishing');
    if (ws.current && ready.current) ws.current.send(JSON.stringify({ type: 'end' }));
    // A connection that is lost now is still tried for a while; after that the text is worked out from the recording on saving.
    finishTimer.current = window.setTimeout(() => setPhase(p => p === 'finishing' ? 'review' : p), 20_000);
  };

  const form = () => {
    const track = join(chunks.current);
    const f = new FormData();
    f.append('audio', toWav(track), 'speech.wav');
    f.append('lengthMs', String(Math.max(1, msOf(track.length))));
    f.append('name', name.trim()); f.append('documentType', docType);
    if (clientRef.trim()) f.append('clientReference', clientRef.trim());
    f.append('language', summaryLanguage(spoken)); f.append('spokenLanguages', spoken);
    f.append('mode', 'speech'); f.append('consentNoticeGiven', 'false'); f.append('markers', '[]');
    if (session.current) f.append('liveSession', session.current);
    const m = median(delays.current);
    if (m !== null) { f.append('delayMedianMs', String(m)); f.append('delayWorstMs', String(Math.max(...delays.current))); }
    return f;
  };

  // Finish: the text goes to Speech to Text (or is worked out from the recording if the live text is not complete).
  const save = async () => {
    setPhase('saving'); setError(''); setMessage('');
    try {
      const f = form();
      closeLink();
      const target = id ? (await api.form(`/items/${id}/draft/finish`, f), id) : (await api.form('/items/recordings', f)).id;
      session.current = null; setDirty(false);
      location.hash = `#/items/${target}`;
    } catch (e: any) { setError(e.message); setPhase('review'); }
  };

  // Draft: saved, not finished. The recording and the text so far are kept; carry on later from the Speech list.
  const saveDraft = async () => {
    setPhase('saving'); setError(''); setMessage('');
    try {
      if (id) await api.form(`/items/${id}/draft`, form());
      else {
        const r = await api.form('/items/drafts', form());
        setId(r.id);
        window.history.replaceState(null, '', `#/speech/live/${r.id}`);
      }
      setDirty(false);
      setMessage('Saved as a draft. Carry on speaking now, or come back to it later from the Speech list.');
    } catch (e: any) { setError(e.message); }
    setPhase('review');
  };

  const removeDraft = async () => {
    setPhase('saving'); setError('');
    try {
      closeLink();
      if (session.current) api.post(`/live/${session.current}/discard`).catch(() => {});
      await api.post(`/items/${id}/delete`, { reason: 'Draft deleted by its author' });
      setDirty(false);
      location.hash = '#/speech';
    } catch (e: any) { setError(e.message); setPhase('review'); setConfirmDiscard(false); }
  };

  const discard = () => {
    closeLink();
    if (session.current) api.post(`/live/${session.current}/discard`).catch(() => {});
    session.current = null; chunks.current = []; samples.current = 0;
    setSegs([]); setPending(0); setElapsed(0); setDelay(null); setNotice(''); setError(''); setConfirmDiscard(false);
    setPhase('setup');
  };

  const needs = !docType ? 'Choose letter, Word document or email to start.'
    : !name.trim() ? (clientRef.trim() ? 'A client reference needs a name as well. Add a name to start.' : 'Give it a name to start.') : '';
  const speaking = phase === 'live' || phase === 'paused';

  return (
    <div className="card">
      <h2>{id ? 'Carry on speaking' : 'Speech to text'}</h2>
      {phase === 'loading' && <p>Opening the draft…</p>}
      {phase === 'setup' && <>
        <p className="note">The text is typed as you speak. Say "comma", "full stop" or "new paragraph" for punctuation, as when dictating.</p>
        <DocumentTypePicker value={docType} onChange={setDocType} />
        <div className="pair"><div className="field"><label>Name<br /><input value={name} onChange={e => setName(e.target.value)} placeholder="e.g. Letter to Mr Smith" /></label></div>
          <div className="field"><label>Client reference (optional)<br /><input value={clientRef} onChange={e => setClientRef(e.target.value)} placeholder="e.g. ACME-042" /></label></div></div>
        <LanguagePicker value={spoken} onChange={setSpoken} allowAuto={false}
          note="Choose the language you will speak. If you add a second or third, the speech service works out which one you are speaking as you go (not yet tried with real speech)." />
        <button className="primary" disabled={!!needs} onClick={start}>Start speaking</button>
        {needs && <p className="need">{needs}</p>}
      </>}
      {phase === 'starting' && <p>Starting the microphone…</p>}

      {phase !== 'setup' && phase !== 'starting' && phase !== 'loading' && <p className="note"><strong>{name}</strong> · {docLabel(docType)}{clientRef ? ` · ${clientRef}` : ''}</p>}

      {speaking && <>
        <div className="timer" aria-live="off">{fmtLen(elapsed)} {phase === 'paused' && <span className="tag warn">Paused</span>}</div>
        <canvas ref={wave} className="wave" role="img" aria-label="Live sound wave from your microphone" />
        {quiet && <div className="banner" role="status">We cannot hear anything yet. Check that the right microphone is selected and that it is not muted.</div>}
        <LinkStatus link={link} delay={delay} notice={notice} />
        <div className="actions" style={{ margin: '12px 0' }}>
          {phase === 'live' && <button onClick={pause}>Pause</button>}
          {phase === 'paused' && <button className="primary" onClick={resume}>Resume</button>}
          <button className="danger" onClick={stop}>Stop</button>
        </div>
        <details className="note"><summary>Spoken commands</summary><CommandHelp /></details>
      </>}
      {phase === 'finishing' && <p role="status">{link === 'lost' ? 'Finishing the text. The connection was lost; trying again…' : 'Finishing the text…'}</p>}

      {phase !== 'setup' && phase !== 'starting' && phase !== 'loading' && <LiveText segs={segs} pending={phase === 'review' || phase === 'saving' ? 0 : pending}
        type={docType || 'document'} name={name} clientRef={clientRef} empty={speaking ? 'Start speaking: the text appears here.' : 'No words were heard.'} />}

      {(phase === 'review' || phase === 'saving') && <>
        <p className="note">Length {fmtLen(elapsed)}.{' '}
          {finished.current ? 'Finish it to correct, approve, assign or export it like a dictation. It then shows under Speech to Text.'
            : 'The live text did not finish, so when you finish, the text is worked out again from the recording. That takes a little longer.'}</p>
        {lost && <div className="banner" role="alert">{lost}</div>}
        {notice && <div className="banner" role="status">{notice}</div>}
        {!confirmDiscard && <div className="actions">
          <button className="primary" disabled={phase === 'saving'} onClick={save}>{phase === 'saving' ? 'Saving…' : 'Finish'}</button>
          <button disabled={phase === 'saving'} onClick={speakMore}>Carry on speaking</button>
          <button disabled={phase === 'saving' || (!dirty && !!id)} onClick={saveDraft}>Save as draft</button>
          <button className="danger" disabled={phase === 'saving'} onClick={() => setConfirmDiscard(true)}>{id ? 'Delete draft' : 'Discard'}</button>
        </div>}
        {confirmDiscard && <div className="banner"><strong>{id ? 'Delete this draft?' : 'Discard this recording and its text?'}</strong>{' '}
          {id ? 'Every saved part is deleted. This cannot be undone.' : 'Nothing has been saved, so it cannot be recovered.'}
          <div className="actions" style={{ marginTop: 8 }}><button className="danger" onClick={id ? removeDraft : discard}>{id ? 'Yes, delete it' : 'Yes, discard it'}</button><button onClick={() => setConfirmDiscard(false)}>No, keep it</button></div></div>}
      </>}
      {message && <div className="ok" role="status">{message}</div>}
      {error && <div className="error">{error}</div>}
    </div>
  );
}

/** One line under the sound wave on how the live text is doing. */
function LinkStatus({ link, delay, notice }: { link: Link; delay: number | null; notice: string }) {
  if (link === 'off') return <div className="banner" role="status">{notice || 'The live text has stopped.'} Keep speaking: everything is still being recorded, and the text is worked out from the recording when you save.</div>;
  if (link === 'lost') return <div className="banner" role="status">The connection was lost. Keep speaking: everything is still being recorded, and the text catches up when the connection is back.</div>;
  if (link === 'connecting') return <p className="note" role="status">Connecting to the speech service… You can start speaking.</p>;
  const behind = delay === null ? '' : ` The text is about ${(delay / 1000).toFixed(1)} s behind your voice.`;
  return <p className="note" role="status">Live.{behind}{delay !== null && delay > 3000 ? ' That is slower than usual: the connection may be slow. Nothing is lost.' : ''}</p>;
}

/** The text so far, laid out as the letter, Word document or email it will be. Words still being heard are lighter. */
function LiveText({ segs, pending, type, name, clientRef, empty }: { segs: LiveSeg[]; pending: number; type: string; name: string; clientRef: string; empty: string }) {
  const paras = useMemo(() => paragraphPieces(segs), [segs]);
  const markers = useMemo(() => paragraphMarkers(segs, paras), [segs, paras]);
  const words = useMemo(() => segs.map(s => s.text.split(/\s+/).filter(Boolean)), [segs]);
  const total = words.reduce((n, w) => n + w.length, 0);
  const firm = total - pending;
  let n = 0;
  const today = new Date().toLocaleDateString('en-GB', { day: 'numeric', month: 'long', year: 'numeric' });
  return (
    <article className="page live-page" aria-label={docLabel(type)} aria-live="polite">
      {type === 'letter' && <><div className="page-date">{today}</div><div className="page-ref">{clientRef ? <>Our ref: {clientRef}</> : null}</div></>}
      {type === 'email' && <div className="mail-head"><div><b>Subject</b> {name}</div>{clientRef && <div><b>Reference</b> {clientRef}</div>}</div>}
      {type === 'document' && <h1 className="page-title">{name}</h1>}
      {total === 0 && <p className="note">{empty}</p>}
      {paras.map((pieces, k) => <div key={k} className={'para' + (markers[k] ? ' list-point' : '')}>
        {markers[k] && <span className="list-marker" aria-hidden="true">{markers[k]}</span>}
        {pieces.flatMap(p => words[p.seg].slice(p.from, p.to)).map((w, i) => {
          const pend = n++ >= firm;
          return <span key={i} className={pend ? 'pending' : undefined}>{i ? ' ' : ''}{w}</span>;
        })}
      </div>)}
    </article>
  );
}
