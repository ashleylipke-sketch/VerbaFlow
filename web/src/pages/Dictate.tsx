import { useEffect, useRef, useState } from 'react';
import { api, fmtLen, type Detail } from '../api';
import LanguagePicker from '../LanguagePicker';
import DocumentTypePicker, { docLabel, type DocType } from '../DocumentType';
import { summaryLanguage } from '../languages';
import { cut, decode, msOf, samplesAt, splice, toWav, type TakeMode } from '../takes';
import WaveEditor, { type Selection } from '../WaveEditor';
import { CommandHelp, drawWave } from './Record';
import { layout, textWords, type Take, type TextWord } from '../takeMap';

type Phase = 'setup' | 'loading' | 'ready' | 'recording' | 'paused' | 'adding';

/**
 * A dictation, recorded like a dictaphone: record, listen back, then add more at the end, put more in at a point, or
 * record over part of it. It can be saved unfinished and carried on later; nothing is converted to text until the
 * author sends it.
 */
export default function Dictate({ draftId }: { draftId?: string }) {
  const [name, setName] = useState('');
  const [clientRef, setClientRef] = useState('');
  const [docType, setDocType] = useState<DocType | ''>('');
  const [spoken, setSpoken] = useState('en-GB');
  const [id, setId] = useState<string | null>(draftId ?? null);
  const [detail, setDetail] = useState<Detail | null>(null);
  const [phase, setPhase] = useState<Phase>(draftId ? 'loading' : 'setup');
  const [trackMs, setTrackMs] = useState(0);
  const [canUndo, setCanUndo] = useState(false);
  const [dirty, setDirty] = useState(false);
  const [url, setUrl] = useState('');
  const [posMs, setPosMs] = useState(0);
  const [playing, setPlaying] = useState(false);
  const [selection, setSelection] = useState<Selection | null>(null);
  // Marking a part by two clicks, to delete it or record over it: first where it starts, then where it ends.
  const [deleteStep, setDeleteStep] = useState<null | 'start' | 'end'>(null);
  const [markFor, setMarkFor] = useState<'delete' | 'overwrite'>('delete');
  const deleteFrom = useRef(0);
  const [elapsed, setElapsed] = useState(0);
  const [quiet, setQuiet] = useState(false);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState('');
  const [message, setMessage] = useState('');
  const [confirmDelete, setConfirmDelete] = useState(false);
  // Changing the recording of a dictation that has been converted but not approved (instead of carrying on with an unfinished one).
  const [changing, setChanging] = useState(false);
  const [corrections, setCorrections] = useState(0);
  const [replaceOk, setReplaceOk] = useState(false);
  // The converted text, so the author can see where to put more in; it follows the changes made on this page.
  const [words, setWords] = useState<TextWord[]>([]);
  const [originalMs, setOriginalMs] = useState(0);
  const [takes, setTakes] = useState<Take[]>([]);

  const track = useRef<Float32Array>(new Float32Array(0));
  const history = useRef<Float32Array[]>([]);
  const takeAt = useRef(0);
  const takeMode_ = useRef<TakeMode | 'replace'>('end');
  const replaceLen = useRef(0); // samples of the marked part a replacement takes out
  const player = useRef<HTMLAudioElement>(null);
  const pendingSeek = useRef(0); // where the cursor goes once a changed recording has loaded
  const playUntil = useRef<number | null>(null);
  const rec = useRef<MediaRecorder | null>(null);
  const chunks = useRef<Blob[]>([]);
  const stream = useRef<MediaStream | null>(null);
  const audioCtx = useRef<AudioContext | null>(null);
  const raf = useRef(0);
  const wave = useRef<HTMLCanvasElement>(null);
  const paused = useRef(false);
  const activeMs = useRef(0);
  const lastTick = useRef(0);

  const stopMic = () => { cancelAnimationFrame(raf.current); stream.current?.getTracks().forEach(t => t.stop()); audioCtx.current?.close().catch(() => {}); audioCtx.current = null; };
  useEffect(() => () => stopMic(), []);
  useEffect(() => () => { if (url) URL.revokeObjectURL(url); }, [url]);

  // Carrying on with a saved dictation: fetch its details and its newest save.
  useEffect(() => {
    if (!draftId) return;
    (async () => {
      try {
        const d = await api.item(draftId);
        setDetail(d);
        const change = d.row.statusCode !== 'Draft';
        if (change && !d.row.actions.includes('change-recording')) { location.hash = `#/items/${draftId}`; return; }
        if (change) {
          setChanging(true);
          const t = await api.transcript(draftId).catch(() => null);
          setCorrections(t ? t.versions.filter(v => v.kind !== 'MachineV1' && v.kind !== 'AutoTidy').length : 0);
          if (t) setWords(textWords(t.segments));
        }
        else if (!d.row.actions.includes('continue')) { setError('Only the person who dictated this can carry on with it.'); setPhase('setup'); return; }
        const audio = await decode(await api.audio(draftId));
        show(audio, false);
        setOriginalMs(msOf(audio.length));
        setDirty(false);
        setPhase('ready');
      } catch (e: any) { setError(e?.message || 'The dictation could not be opened. Try again.'); setPhase('setup'); }
    })();
  }, [draftId]);

  // Leaving with unsaved recording loses it, so the browser asks first.
  useEffect(() => {
    if (!dirty) return;
    const warn = (e: BeforeUnloadEvent) => { e.preventDefault(); e.returnValue = ''; };
    window.addEventListener('beforeunload', warn);
    return () => window.removeEventListener('beforeunload', warn);
  }, [dirty]);

  useEffect(() => {
    if (phase !== 'recording') return;
    lastTick.current = performance.now();
    const t = setInterval(() => { const n = performance.now(); activeMs.current += n - lastTick.current; lastTick.current = n; setElapsed(activeMs.current); }, 200);
    return () => { clearInterval(t); activeMs.current += performance.now() - lastTick.current; };
  }, [phase]);

  /** Shows a new version of the dictation; `undoable` keeps the previous one for Undo. */
  const show = (next: Float32Array, undoable: boolean, cursorMs = 0) => {
    if (undoable) { history.current.push(track.current); setCanUndo(true); }
    track.current = next;
    setTrackMs(msOf(next.length));
    setUrl(next.length ? URL.createObjectURL(toWav(next)) : '');
    pendingSeek.current = cursorMs;
    setPosMs(cursorMs);
    setSelection(null);
    setDirty(true);
  };

  /** Takes the marked part out of the recording. */
  const deleteMarked = () => {
    if (!selection) return;
    const { from, to } = selection;
    player.current?.pause();
    show(cut(track.current, samplesAt(from), samplesAt(to)), true, from);
    setTakes(t => [...t, { at: from, mode: 'delete', len: to - from }]);
    setMessage(`Deleted ${fmtFine(to - from)} from ${fmtFine(from)}. Not saved yet.`);
  };

  const play = () => {
    const p = player.current; if (!p) return;
    if (!p.paused) { p.pause(); return; }
    playUntil.current = selection ? selection.to : null;
    p.currentTime = (selection ? selection.from : posMs >= trackMs - 50 ? 0 : posMs) / 1000;
    p.play().catch(() => {});
  };

  // Playing a marked part stops at its end.
  useEffect(() => {
    if (!playing || playUntil.current === null) return;
    let raf = 0;
    const tick = () => {
      const p = player.current;
      if (p && playUntil.current !== null && p.currentTime * 1000 >= playUntil.current) { p.pause(); p.currentTime = playUntil.current / 1000; return; }
      raf = requestAnimationFrame(tick);
    };
    tick();
    return () => cancelAnimationFrame(raf);
  }, [playing]);

  const undo = () => {
    const prev = history.current.pop();
    if (!prev) return;
    show(prev, false);
    setTakes(t => t.slice(0, -1));
    setCanUndo(history.current.length > 0);
    setMessage('The last change was undone.');
  };

  const start = async (wanted: TakeMode | 'replace' = 'end') => {
    setError(''); setMessage('');
    const p = player.current; if (p && !p.paused) p.pause();
    const mode = track.current.length === 0 ? 'end' : wanted === 'replace' && !selection ? 'overwrite' : wanted;
    takeMode_.current = mode;
    takeAt.current = mode === 'end' ? track.current.length : samplesAt(mode === 'replace' ? selection!.from : posMs);
    replaceLen.current = mode === 'replace' ? samplesAt(selection!.to) - samplesAt(selection!.from) : 0;
    try {
      const s = await navigator.mediaDevices.getUserMedia({ audio: { channelCount: 1, echoCancellation: false, noiseSuppression: false, autoGainControl: false } });
      stream.current = s;
      const ctx = new AudioContext(); audioCtx.current = ctx;
      const an = ctx.createAnalyser(); an.fftSize = 512; ctx.createMediaStreamSource(s).connect(an);
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
      const r = new MediaRecorder(s); rec.current = r; chunks.current = [];
      r.ondataavailable = e => e.data.size && chunks.current.push(e.data);
      r.onstop = () => addTake(new Blob(chunks.current, { type: r.mimeType || 'audio/webm' }));
      r.start(1000); activeMs.current = 0; setElapsed(0); paused.current = false; setPhase('recording');
    } catch (e: any) {
      stopMic();
      setError(e?.name === 'NotAllowedError' ? 'VerbaFlow does not have permission to use the microphone. Allow it in your browser, then try again.' : 'The microphone could not be started. Check that one is connected and not in use by another app, then try again.');
    }
  };

  const pause = () => { rec.current?.pause(); paused.current = true; setPhase('paused'); };
  const resume = () => { rec.current?.resume(); paused.current = false; setPhase('recording'); };
  const stop = () => { setPhase('adding'); rec.current?.stop(); stopMic(); setQuiet(false); };

  const addTake = async (blob: Blob) => {
    try {
      const take = await decode(blob);
      // The cursor ends up just after the new recording, ready to play on from there or add more.
      const mode = takeMode_.current;
      // Recording over a marked part: the part comes out and the new recording goes in its place, whatever its length.
      const next = mode === 'replace'
        ? splice(cut(track.current, takeAt.current, takeAt.current + replaceLen.current), take, takeAt.current, 'insert')
        : splice(track.current, take, takeAt.current, mode);
      show(next, true, msOf((mode === 'end' ? track.current.length : takeAt.current) + take.length));
      setTakes(t => [...t, { at: msOf(takeAt.current), mode, len: msOf(take.length), cut: mode === 'replace' ? msOf(replaceLen.current) : undefined }]);
      const at = fmtLen(msOf(takeAt.current));
      setMessage(`${mode === 'insert' ? `Put in at ${at}` : mode === 'overwrite' ? `Recorded over from ${at}` : mode === 'replace' ? `Marked part replaced from ${at}` : 'Added'}. Not saved yet.`);
    } catch { setError('That recording could not be added. Try recording it again.'); }
    setPhase('ready');
  };

  const form = (finish: boolean) => {
    const f = new FormData();
    f.append('audio', toWav(track.current), 'dictation.wav');
    f.append('lengthMs', String(Math.max(1, msOf(track.current.length))));
    if (!id) {
      f.append('name', name.trim()); f.append('documentType', docType);
      if (clientRef.trim()) f.append('clientReference', clientRef.trim());
      f.append('language', summaryLanguage(spoken)); f.append('spokenLanguages', spoken);
      if (finish) { f.append('mode', 'dictate'); f.append('consentNoticeGiven', 'false'); f.append('markers', '[]'); }
    }
    return f;
  };

  const saveDraft = async () => {
    setBusy(true); setError(''); setMessage('');
    try {
      if (id) await api.form(`/items/${id}/draft`, form(false));
      else {
        const r = await api.form('/items/drafts', form(false));
        setId(r.id);
        replaceHash(`#/dictate/record/${r.id}`);
      }
      setDirty(false);
      setMessage('Saved. Carry on now, or come back to it later from the Dictate list.');
    } catch (e: any) { setError(e.message); } finally { setBusy(false); }
  };

  const finish = async () => {
    setBusy(true); setError(''); setMessage('');
    try {
      const target = id ?? (await api.form('/items/recordings', form(true))).id;
      if (id) await api.form(`/items/${id}/draft/finish`, form(true));
      setDirty(false);
      location.hash = `#/items/${target}`;
    } catch (e: any) { setError(e.message); } finally { setBusy(false); }
  };

  /** Sends the changed recording; the dictation is converted to text again. */
  const convertAgain = async () => {
    setBusy(true); setError(''); setMessage('');
    try {
      const f = form(true);
      if (corrections > 0) f.append('discardEdits', String(replaceOk));
      await api.form(`/items/${id}/recording`, f);
      setDirty(false);
      location.hash = `#/items/${id}`;
    } catch (e: any) { setError(e.message); } finally { setBusy(false); }
  };

  const remove = async () => {
    setBusy(true); setError('');
    try {
      if (id) await api.post(`/items/${id}/delete`, { reason: 'Unfinished dictation deleted by its author' });
      setDirty(false);
      location.hash = '#/dictate';
    } catch (e: any) { setError(e.message); } finally { setBusy(false); setConfirmDelete(false); }
  };

  const dictateNeeds = !docType ? 'Choose letter, Word document or email to start.'
    : !name.trim() ? (clientRef.trim() ? 'A client reference needs a name as well. Add a name to start.' : 'Give the dictation a name to start.') : '';
  const hasAudio = trackMs > 0;
  const recordingNow = phase === 'recording' || phase === 'paused';
  const title = detail?.row.name ?? name;
  const type = detail?.documentType ?? docType;
  const ref = detail?.clientReference ?? clientRef;

  const seek = (at: number) => {
    const ms = Math.max(0, Math.min(trackMs, at)); // word times can run a little past the end of the recording
    const p = player.current;
    if (p) p.currentTime = ms / 1000;
    setPosMs(ms);
  };
  /** While deleting by two clicks: the first click is the start, the second the end (either order). */
  const pointClick = (at: number) => {
    const ms = Math.max(0, Math.min(trackMs, at));
    if (deleteStep === 'start') { deleteFrom.current = ms; setSelection(null); seek(ms); setDeleteStep('end'); return; }
    const a = Math.min(deleteFrom.current, ms), b = Math.max(deleteFrom.current, ms);
    setDeleteStep(null);
    if (b - a < 20) { setSelection(null); return; }
    setSelection({ from: a, to: b }); seek(a);
  };
  /** A click on a word puts the cursor just before it; Shift + click marks from the cursor to the end of that word. */
  const clickWord = (e: React.MouseEvent | React.KeyboardEvent, from: number, to: number) => {
    if (deleteStep) { pointClick(deleteStep === 'start' || to <= deleteFrom.current ? from : to); return; }
    if (e.shiftKey) {
      const a = Math.min(posMs, from), b = Math.max(posMs, to);
      setSelection(b > a ? { from: a, to: b } : null);
      seek(a);
    } else { setSelection(null); seek(from); }
  };
  const shown = changing && words.length ? layout(words, takes, originalMs) : [];
  const paras: (typeof shown)[] = [];
  // A new recording just before a paragraph's first word belongs to that paragraph.
  for (const x of shown) {
    const last = paras[paras.length - 1];
    if (!last || (x.kind === 'word' && x.paraStart && last.some(y => y.kind === 'word'))) paras.push([]);
    paras[paras.length - 1].push(x);
  }
  const nowIndex = shown.findIndex(x => x.kind === 'word' && !x.gone && posMs >= x.startMs && posMs < x.endMs);
  const marked = (x: { startMs: number; endMs: number }) => !!selection && (x.startMs + x.endMs) / 2 >= selection.from && (x.startMs + x.endMs) / 2 < selection.to;

  return (
    <div className="card">
      <h2>{changing ? 'Insert or amend' : draftId ? 'Carry on dictating' : 'New dictation'}</h2>
      {changing && phase === 'ready' && <p className="note">Put the cursor where you want to make a change, then put more in, record over, or mark a part and delete it.
        When you are done, the dictation is converted to text again. The recording as it was is kept in the history.</p>}
      {phase === 'loading' && <p>Opening the dictation…</p>}

      {phase === 'setup' && !draftId && <>
        <DocumentTypePicker value={docType} onChange={setDocType} />
        <div className="pair"><div className="field"><label>Name<br /><input value={name} onChange={e => setName(e.target.value)} placeholder="e.g. Letter to Mr Smith" /></label></div>
          <div className="field"><label>Client reference (optional)<br /><input value={clientRef} onChange={e => setClientRef(e.target.value)} placeholder="e.g. ACME-042" /></label></div></div>
        <LanguagePicker value={spoken} onChange={setSpoken} />
        <button className="primary" disabled={!!dictateNeeds} onClick={() => start()}>Start dictating</button>
        {dictateNeeds && <p className="need">{dictateNeeds}</p>}
      </>}

      {phase !== 'setup' && phase !== 'loading' && <p className="note"><strong>{title}</strong> · {docLabel(type)}{ref ? ` · ${ref}` : ''}</p>}

      {recordingNow && <>
        <div className="timer" aria-live="off">{fmtLen(elapsed)} {phase === 'paused' && <span className="tag warn">Paused</span>}</div>
        {hasAudio && <p className="note">{takeMode_.current === 'insert' ? `Putting this in at ${fmtLen(msOf(takeAt.current))}.`
          : takeMode_.current === 'overwrite' ? `Recording over what you said from ${fmtLen(msOf(takeAt.current))}.`
          : takeMode_.current === 'replace' ? `Recording what goes in place of the marked part (${fmtFine(msOf(replaceLen.current))} from ${fmtFine(msOf(takeAt.current))}). It can be shorter or longer.` : 'Adding to the end.'}</p>}
        <details className="note"><summary>Spoken commands</summary><CommandHelp /></details>
        <canvas ref={wave} className="wave" role="img" aria-label="Live sound wave from your microphone" />
        {quiet && <div className="banner" role="status">We cannot hear anything yet. Check that the right microphone is selected and that it is not muted.</div>}
        <div className="actions" style={{ margin: '12px 0' }}>
          {phase === 'recording' && <button onClick={pause}>Pause</button>}
          {phase === 'paused' && <button className="primary" onClick={resume}>Resume</button>}
          <button className="danger" onClick={stop}>Stop</button>
        </div>
      </>}
      {phase === 'adding' && <p>Adding the recording…</p>}

      {phase === 'ready' && <>
        <p className="note">Length {fmtLen(trackMs)}.{changing ? (dirty ? ' Changed, not sent yet.' : '') : dirty ? ' Not saved yet.' : ' Saved.'}</p>
        {url && <audio ref={player} src={url} preload="auto" style={{ display: 'none' }}
          onLoadedMetadata={e => { if (pendingSeek.current) e.currentTarget.currentTime = pendingSeek.current / 1000; }}
          onPlay={() => setPlaying(true)} onPause={() => setPlaying(false)} onEnded={() => setPlaying(false)}
          onTimeUpdate={e => setPosMs(Math.round(e.currentTarget.currentTime * 1000))} />}
        {hasAudio && <>
          <WaveEditor track={track.current} lengthMs={trackMs} now={() => (player.current?.currentTime ?? 0) * 1000} playing={playing}
            cursorMs={posMs} selection={selection} onSeek={deleteStep ? pointClick : seek} onSelect={deleteStep ? () => {} : setSelection} />
          <div className="actions wave-bar">
            <button onClick={play}>{playing ? 'Pause' : selection ? 'Play marked part' : 'Play from cursor'}</button>
            <span className="note">Cursor {fmtFine(posMs)} of {fmtFine(trackMs)}{selection ? ` · marked ${fmtFine(selection.from)} to ${fmtFine(selection.to)}` : ''}</span>
          </div>
          {deleteStep && <div className="banner" role="status"><strong>{markFor === 'delete' ? 'Delete a part' : 'Record over a part'}:</strong> {deleteStep === 'start'
            ? `click where the ${markFor === 'delete' ? 'deletion' : 'part to record over'} should start, on the sound wave${shown.length ? ' or on a word' : ''}.`
            : `now click where it should end. It starts at ${fmtFine(deleteFrom.current)}.`}{' '}
            <button className="link" onClick={() => setDeleteStep(null)}>Cancel</button></div>}
          <p className="note">Click the sound wave{shown.length ? ' or a word in the text' : ''} to put the cursor there. Drag across the sound wave{shown.length ? ', or Shift + click a word,' : ''} to mark a part.</p>
        </>}
        {shown.length > 0 && <div className="take-text">
          <p className="note">The text as it was converted. A click on a word puts the cursor just before it.{takes.length ? ' Words deleted or recorded over are crossed out; new recordings are marked and will be converted when you send them.' : ''}</p>
          {paras.map((p, k) => <p key={k} className="words">{p.map((x, i) => x.kind === 'new'
            ? <span key={i} className="tag new-take" role="button" tabIndex={0} onClick={() => { setSelection(null); seek(x.startMs); }} onKeyDown={e => e.key === 'Enter' && seek(x.startMs)}>New recording {x.take} ({fmtLen(x.endMs - x.startMs)})</span>
            : x.gone ? <del key={i}>{x.text} </del>
            : <span key={i}><span className={'w' + (shown.indexOf(x) === nowIndex ? ' now' : '') + (marked(x) ? ' marked' : '')} role="button" tabIndex={0}
                onClick={e => clickWord(e, x.startMs, x.endMs)} onKeyDown={e => e.key === 'Enter' && clickWord(e, x.startMs, x.endMs)}>{x.text}</span> </span>)}</p>)}
        </div>}
        <div className="actions" style={{ margin: '12px 0' }}>
          {!hasAudio && <button className="primary" disabled={busy} onClick={() => start()}>Record</button>}
          {hasAudio && <>
            <button className="primary" disabled={busy} onClick={() => start('insert')}>Insert at {fmtFine(posMs)}</button>
            <button disabled={busy} onClick={() => start('end')}>Add to the end</button>
          </>}
        </div>
        {hasAudio && <div className="actions" style={{ margin: '0 0 12px' }}>
          {selection
            ? <>
              <button className={markFor === 'overwrite' ? 'primary' : ''} disabled={busy} onClick={() => start('replace')}>Record over marked part</button>
              <button className="danger" disabled={busy} onClick={deleteMarked}>Delete marked part ({fmtFine(selection.to - selection.from)})</button>
            </>
            : <>
              <button className="danger" disabled={busy || !!deleteStep} onClick={() => { player.current?.pause(); setSelection(null); setMarkFor('delete'); setDeleteStep('start'); }}>Delete…</button>
              <button disabled={busy || !!deleteStep} onClick={() => { player.current?.pause(); setSelection(null); setMarkFor('overwrite'); setDeleteStep('start'); }}>Overwrite…</button>
              <button disabled={busy || posMs >= trackMs - 50} onClick={() => setSelection({ from: posMs, to: trackMs })}>Mark from cursor to the end</button>
            </>}
          {selection && <button disabled={busy} onClick={() => setSelection(null)}>Clear the mark</button>}
          {canUndo && <button disabled={busy} onClick={undo}>Undo last change</button>}
        </div>}
        {changing && <>
          {corrections > 0 && <label className="row"><input type="checkbox" checked={replaceOk} onChange={e => setReplaceOk(e.target.checked)} />
            <span>The text has {corrections} saved correction{corrections > 1 ? 's' : ''}. I understand converting again replaces {corrections > 1 ? 'them' : 'it'}.</span></label>}
          <div className="actions">
            <button className="primary" disabled={busy || !dirty || (corrections > 0 && !replaceOk)} onClick={convertAgain}>{busy ? 'Sending…' : 'Convert to text again'}</button>
            <a className="btn" href={`#/items/${id}`}>Cancel</a>
          </div>
        </>}
        {!changing && hasAudio && !confirmDelete && <div className="actions">
          <button className="primary" disabled={busy} onClick={finish}>{busy ? 'Saving…' : 'Finish and convert to text'}</button>
          <button disabled={busy || (!dirty && !!id)} onClick={saveDraft}>Save and finish later</button>
          <button className="danger" disabled={busy} onClick={() => setConfirmDelete(true)}>{id ? 'Delete dictation' : 'Discard'}</button>
        </div>}
        {confirmDelete && <div className="banner"><strong>{id ? 'Delete this unfinished dictation?' : 'Discard this recording?'}</strong>{' '}
          {id ? 'Every saved part is deleted. This cannot be undone.' : 'Nothing has been saved, so it cannot be recovered.'}
          <div className="actions" style={{ marginTop: 8 }}><button className="danger" onClick={remove}>{id ? 'Yes, delete it' : 'Yes, discard it'}</button><button onClick={() => setConfirmDelete(false)}>No, keep it</button></div></div>}
      </>}

      {message && <div className="ok" role="status">{message}</div>}
      {error && <div className="error">{error}</div>}
    </div>
  );
}

/** A time to a tenth of a second (0:12.4), for putting the cursor at an exact point. */
function fmtFine(ms: number) {
  const t = Math.max(0, Math.round(ms / 100));
  const m = Math.floor(t / 600), sec = (t % 600) / 10;
  return `${m}:${sec < 10 ? '0' : ''}${sec.toFixed(1)}`;
}

/** Puts the saved dictation's address in the location bar without reopening the page. */
function replaceHash(hash: string) { window.history.replaceState(null, '', hash); }

