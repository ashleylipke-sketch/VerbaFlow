import AttachmentsCard from '../AttachmentsCard';
import RetentionLine from '../RetentionLine';
import type { RefObject } from 'react';
import { createContext, useCallback, useContext, useEffect, useMemo, useRef, useState } from 'react';
import LanguagePicker from '../LanguagePicker';
import { ExportMenu } from '../ExportMenu';
import GrammarCard from '../GrammarCard';
import { docLabel } from '../DocumentType';
import { paragraphMarkers, paragraphPieces, type Piece } from '../paragraphs';
import { api, describeAudit, fmtDate, fmtLen, type AuditEvent, type Detail, type Outputs, type Transcript, type UserView } from '../api';
import { activeSegmentIndex, activeWordIndex, wordsFor } from '../words';
import { diffWords } from '../diff';
import PlayerBar from '../PlayerBar';
import { countIn, swapsIn } from '../replace';

/** A textarea grows to show all its text, so nobody has to scroll inside it. */
function useFit(text: string) {
  const ref = useRef<HTMLTextAreaElement>(null);
  useEffect(() => { const el = ref.current; if (!el) return; el.style.height = 'auto'; el.style.height = `${el.scrollHeight + 2}px`; }, [text]);
  return ref;
}

/** Lets an Edit button pause the recording while the person types, and carry on playing once they save or cancel. */
const EditPause = createContext<{ pause: () => void; resume: () => void }>({ pause: () => undefined, resume: () => undefined });

export default function ItemPage({ id }: { id: string }) {
  const [d, setD] = useState<Detail | null>(null);
  const [t, setT] = useState<Transcript | null>(null);
  const [o, setO] = useState<Outputs | null>(null);
  const [audit, setAudit] = useState<AuditEvent[]>([]);
  const [users, setUsers] = useState<UserView[]>([]);
  const [audioUrl, setAudioUrl] = useState('');
  const [error, setError] = useState('');
  const [busy, setBusy] = useState(false);
  const [retryLang, setRetryLang] = useState('');
  const [rerunSpeakers, setRerunSpeakers] = useState('');
  const [rerunMethod, setRerunMethod] = useState('');
  const [rerunAudio, setRerunAudio] = useState('');
  const [rerunLang, setRerunLang] = useState('');
  const [rerunService, setRerunService] = useState('');
  const [assignTo, setAssignTo] = useState('');
  const [reason, setReason] = useState('');
  const [reopenReason, setReopenReason] = useState('');
  const [deleteReason, setDeleteReason] = useState('');
  const [showAudit, setShowAudit] = useState(false);
  const player = useRef<HTMLMediaElement>(null);
  const [pos, setPos] = useState({ seg: -1, word: -1 });
  const [playing, setPlaying] = useState(false);
  // Tracked changes show while the transcript is a draft and are tucked away once it is approved. The reader can override either way.
  const [changesChoice, setChangesChoice] = useState<boolean | null>(null);
  // After a correction ("Rhys" to "Reiss"), the same words elsewhere in the transcript, offered for changing in one go.
  const [offers, setOffers] = useState<{ from: string; to: string; count: number }[]>([]);

  const load = useCallback(async () => {
    try {
      const det = await api.item(id); setD(det);
      api.users().then(setUsers);
      api.transcript(id).then(setT).catch(() => setT(null));
      api.outputs(id).then(setO).catch(() => setO(null));
      api.audit(id).then(setAudit).catch(() => setAudit([]));
    } catch (e: any) { setError(e.message); }
  }, [id]);
  useEffect(() => { load(); const i = setInterval(() => { if (d?.processingState === 'Processing') load(); }, 3000); return () => clearInterval(i); }, [load, d?.processingState]);
  // A meeting video is too big to load whole: it plays through a short-lived link that streams it. Audio loads whole.
  const isVideo = d === null ? null : d.video;
  const removed = !!d?.recordingRemoved;
  useEffect(() => {
    if (isVideo === null || removed) return;
    let url = '';
    if (isVideo) api.post(`/items/${id}/play`).then(r => setAudioUrl(r.url)).catch(() => setAudioUrl(''));
    else api.audio(id).then(b => { url = URL.createObjectURL(b); setAudioUrl(url); }).catch(() => setAudioUrl(''));
    return () => { if (url) URL.revokeObjectURL(url); };
  }, [id, isVideo, removed]);

  // Follow the audio: read its position about 60 times a second while it plays, and only re-render when the word changes.
  const segs = t?.segments ?? [];
  const timed = useMemo(() => segs.map(sg => wordsFor(sg)), [t]); // eslint-disable-line react-hooks/exhaustive-deps
  const track = useCallback(() => {
    const a = player.current;
    if (!a || !t) return;
    const ms = a.currentTime * 1000;
    const seg = activeSegmentIndex(t.segments, ms);
    const word = seg < 0 ? -1 : activeWordIndex(timed[seg], ms);
    setPos(p => (p.seg === seg && p.word === word ? p : { seg, word }));
  }, [t, timed]);
  useEffect(() => {
    const a = player.current;
    if (!a) return;
    let raf = 0;
    const loop = () => { track(); raf = requestAnimationFrame(loop); };
    const onPlay = () => { setPlaying(true); cancelAnimationFrame(raf); loop(); };
    const onStop = () => { setPlaying(false); cancelAnimationFrame(raf); track(); };
    a.addEventListener('play', onPlay); a.addEventListener('pause', onStop); a.addEventListener('ended', onStop); a.addEventListener('seeked', track);
    if (!a.paused) onPlay();
    return () => { cancelAnimationFrame(raf); a.removeEventListener('play', onPlay); a.removeEventListener('pause', onStop); a.removeEventListener('ended', onStop); a.removeEventListener('seeked', track); };
  }, [track, audioUrl]);
  // Recordings made in the browser have no length stored in the file, which stops seeking. Reading to the end once fixes it.
  const fixDuration = () => {
    const a = player.current;
    if (!a || a.duration !== Infinity) return;
    const back = () => { a.removeEventListener('timeupdate', back); a.currentTime = 0; };
    a.addEventListener('timeupdate', back);
    a.currentTime = 1e101;
  };
  const approved = d?.row.statusCode === 'Completed' || d?.row.statusCode === 'Purged';
  const showChanges = changesChoice ?? !approved;
  const editedCount = segs.filter(x => x.originalText !== null).length;
  // A person's saved corrections; the app's own versions (machine text, crossed-out restarts) do not count.
  const corrections = t ? t.versions.filter(v => v.kind !== 'MachineV1' && v.kind !== 'AutoTidy').length : 0;
  // Only playback that Edit stopped is started again; a recording that was not playing stays stopped.
  const pausedForEdit = useRef(false);
  const editPause = useMemo(() => ({
    pause: () => { const a = player.current; if (a && !a.paused) { a.pause(); pausedForEdit.current = true; } },
    resume: () => { if (!pausedForEdit.current) return; pausedForEdit.current = false; player.current?.play().catch(() => undefined); },
  }), []);
  // Ctrl + Space plays or pauses from anywhere on the page, including while typing a correction.
  useEffect(() => {
    const key = (e: KeyboardEvent) => {
      if (!e.ctrlKey || e.code !== 'Space') return;
      const a = player.current; if (!a) return;
      e.preventDefault();
      if (a.paused) a.play().catch(() => undefined); else a.pause();
    };
    addEventListener('keydown', key);
    return () => removeEventListener('keydown', key);
  }, []);
  // Looks at what a saved correction swapped, and counts the same words in the other boxes.
  const offerSwaps = (edits: [string, string][]) => {
    if (!t) return;
    const found: { from: string; to: string; count: number }[] = [];
    for (const [sid, text] of edits) {
      const before = t.segments.find(x => x.id === sid)?.text ?? '';
      const others = t.segments.filter(x => !edits.some(([e]) => e === x.id)).map(x => x.text);
      for (const sw of swapsIn(before, text)) {
        const count = countIn(others, sw.from);
        if (count > 0 && !found.some(f => f.from === sw.from && f.to === sw.to)) found.push({ ...sw, count });
      }
    }
    setOffers(found.slice(0, 3));
  };
  const replaceAll = (from: string, to: string) => run(async () => {
    await api.post(`/items/${id}/replace`, { from, to });
    setOffers(v => v.filter(x => x.from !== from || x.to !== to));
  });
  const seek = (ms: number) => { const a = player.current; if (!a) return; a.currentTime = ms / 1000; a.play().catch(() => undefined); };

  const run = async (fn: () => Promise<unknown>) => { setError(''); try { await fn(); await load(); } catch (e: any) { setError(e.message); } };
  const download = () => run(async () => { const b = await api.audio(id, true); const a = document.createElement('a'); a.href = URL.createObjectURL(b); a.download = d!.originalFileName ?? 'audio'; a.click(); });

  if (!d) return <div className="card">{error ? <span className="error">{error}</span> : 'Loading…'}</div>;
  const r = d.row, acts = new Set(r.actions);
  const assignee = users.filter(u => !u.isSupport);
  // A dictation is one person's text: no speakers to name, no summary, and Export gives the text alone.
  // Speech mode's items are dictations whose text was typed live: shown and handled the same way.
  const dictate = d.mode !== 'Meeting';

  return (
    <EditPause.Provider value={editPause}>
      <p>{d.mode === 'Speech' ? <a href="#/speech">← Back to Speech</a> : dictate ? <a href="#/dictate">← Back to Dictate</a> : <a href="#/">← Back to Meeting</a>}</p>
      <div className="card">
        <div className="item-head">
          <h2>{r.name} {r.tags.map(x => <span key={x} className="tag">{x}</span>)}</h2>
          {d.canExport && t && <ExportMenu id={id} approved={approved} dictate={dictate} documentType={d.documentType} onDone={load} />}
        </div>
        <div className="note">
          <span className="pill">{r.status}</span> · {fmtLen(r.lengthMs)} · Owner {d.owner} · You are: {d.role} · Created {fmtDate(r.createdOn)}
          {r.assignedTo && <> · Assigned to {r.assignedTo}</>}
          {d.attendees && d.attendees.length > 0 && <> · Also for {d.attendees.join(', ')}</>}
          {d.approvedAt && <> · Approved by {d.approvedBy} on {fmtDate(d.approvedAt)}</>}
        </div>
        {d.deletedAt && <div className="banner">This recording was deleted by {d.deletedBy} on {fmtDate(d.deletedAt)}. Reason: {d.deleteReason}. Its audio, transcript and summary have been removed; the history below is kept.</div>}
        {d.recordingRemoved && <div className="banner">{d.recordingRemoved}</div>}
        {!d.deletedAt && <RetentionLine id={id} />}
        {acts.has('change-recording') && <p className="note">Need to add something or re-record part of it? <a className="btn primary" href={`#/dictate/record/${id}`}>Insert or Amend</a></p>}
        {r.statusCode === 'Draft' && <div className="banner">This {d.mode === 'Speech' ? 'item' : 'dictation'} is not finished yet, so it has not been converted to text.
          {acts.has('continue') ? <> <a className="btn primary" href={d.mode === 'Speech' ? `#/speech/live/${id}` : `#/dictate/record/${id}`}>{d.mode === 'Speech' ? 'Carry on speaking' : 'Carry on dictating'}</a></>
            : ` Only the person who ${d.mode === 'Speech' ? 'spoke' : 'dictated'} it can carry on with it.`}</div>}
        {d.processingState === 'Processing' && <div className="banner">Transcribing… this page updates itself when it is ready.</div>}
        {d.failureReason && <div className="banner">Conversion failed: {d.failureReason}
          {acts.has('retry') && <div className="actions" style={{ marginTop: 8 }}>
            <LanguagePicker value={retryLang || d.spokenLanguages} onChange={setRetryLang} />
            <button className="primary" onClick={() => run(() => api.post(`/items/${id}/retry`, { spokenLanguages: retryLang || d.spokenLanguages }))}>Retry conversion</button>
            <span className="note">The recording is safe. Retrying starts the conversion again from the original audio.</span></div>}</div>}
        {d.markers.length > 0 && <div className="note">Markers: {d.markers.map(m => `${fmtLen(m.offsetMs)} ${m.type}${m.note ? ` (${m.note})` : ''}`).join(' · ')}</div>}
        {d.chain.length > 1 && <div className="note">Versions: {d.chain.map(c => <span key={c.id}>{c.isCurrent ? <b>v{c.versionNo}</b> : <a href={`#/items/${c.id}`}>v{c.versionNo}</a>} ({c.status}){' '}</span>)}</div>}
        {audioUrl && d.canDownloadAudio && <div className="actions" style={{ marginTop: 8 }}><button onClick={download}>{d.video ? 'Download original video' : 'Download original audio'}</button></div>}
        {error && <div className="error">{error}</div>}
        <div className="actions" style={{ marginTop: 12, flexWrap: 'wrap' }}>
          {acts.has('accept') && <button className="primary" onClick={() => run(() => api.post(`/items/${id}/accept`))}>Accept</button>}
          {acts.has('return') && <button onClick={() => run(() => api.post(`/items/${id}/return`))}>Return to owner</button>}
          {acts.has('approve') && <button className="primary" onClick={() => { if (confirm('Approving locks this item. Continue?')) run(() => api.post(`/items/${id}/approve`)); }}>Approve</button>}
        </div>
        {(acts.has('assign') || acts.has('reassign')) && <div style={{ marginTop: 12 }}>
          <h3>{acts.has('reassign') ? 'Reassign' : 'Assign'}</h3>
          <div className="actions" style={{ flexWrap: 'wrap' }}>
            <select value={assignTo} onChange={e => setAssignTo(e.target.value)}>
              <option value="">Choose a person…</option><option value="none">Nobody (take back)</option>
              {assignee.map(u => <option key={u.id} value={u.id}>{u.name}</option>)}
            </select>
            {acts.has('reassign') && <input placeholder="Reason (required)" value={reason} onChange={e => setReason(e.target.value)} />}
            <button disabled={!assignTo} onClick={() => run(() => api.post(`/items/${id}/assign`, { userId: assignTo === 'none' ? null : assignTo, reason: reason || null }))}>Apply</button>
          </div></div>}
        {acts.has('request-reopen') && <div style={{ marginTop: 12 }}>
          <h3>Reopen</h3>
          <div className="actions"><input placeholder="Why does this need reopening?" value={reopenReason} onChange={e => setReopenReason(e.target.value)} style={{ minWidth: 280 }} />
            <button disabled={!reopenReason.trim()} onClick={() => run(async () => { await api.post(`/items/${id}/reopen`, { reason: reopenReason }); setReopenReason(''); })}>Request reopen</button></div>
          <div className="note">Needs approval from two different administrators.</div></div>}
        {(acts.has('delete') || acts.has('request-delete')) && <div style={{ marginTop: 12 }}>
          <h3>Delete</h3>
          <div className="actions"><input placeholder="Why should this be deleted?" value={deleteReason} onChange={e => setDeleteReason(e.target.value)} style={{ minWidth: 280 }} />
            {acts.has('delete')
              ? <button className="danger" disabled={!deleteReason.trim()} onClick={() => {
                  if (confirm('This permanently removes the recording, its transcript and its summary. Only a record that it was deleted, by whom and why, is kept. Continue?'))
                    run(async () => { await api.post(`/items/${id}/delete`, { reason: deleteReason }); setDeleteReason(''); setAudioUrl(''); });
                }}>Delete</button>
              : <button className="danger" disabled={!deleteReason.trim()} onClick={() => run(async () => { await api.post(`/items/${id}/delete-request`, { reason: deleteReason }); setDeleteReason(''); })}>Ask to delete</button>}</div>
          <div className="note">{acts.has('delete')
            ? 'Removes the recording, transcript and summary for good. The history keeps a record of the deletion.'
            : 'This recording was approved before, so two different administrators must agree (an administrator who asks counts as one). Every version, including the approved one, is then removed for good.'}</div></div>}
      </div>

      {!d.deletedAt && <AttachmentsCard id={id} onChange={() => api.audit(id).then(setAudit).catch(() => {})} />}

      <div className="withPlayer">
      {audioUrl && <div className={d.video ? 'player video' : 'player'}>{d.video
        ? <video ref={player as RefObject<HTMLVideoElement>} controls playsInline preload="metadata" src={audioUrl} />
        : <audio ref={player as RefObject<HTMLAudioElement>} controls src={audioUrl} onLoadedMetadata={fixDuration} style={{ width: '100%' }} />}
        <PlayerBar player={player} playing={playing} /></div>}
      <div className="split">
        <div className="card">
          <h2>{dictate ? docLabel(d.documentType) : 'Transcript'} {t && <span className="note">version {t.versionNo} · {t.engine}</span>}</h2>
          {!t && <p className="note">{d.processingState === 'Processing' ? 'Not ready yet.' : 'No transcript.'}</p>}
          {t && !dictate && <p className="note">Speakers: {t.speakers.map(s => (
            <span key={s.id} style={{ marginRight: 10 }}>{s.name}{d.canEditTranscript && <> <button onClick={() => { const n = prompt('Rename speaker', s.name); if (n?.trim()) run(() => api.put(`/items/${id}/speakers/${s.id}`, { name: n.trim() })); }}>Rename</button></>}</span>))}</p>}
          {t && editedCount > 0 && <div className="changes-bar">
            <label><input type="checkbox" checked={showChanges} onChange={e => setChangesChoice(e.target.checked)} /> Show tracked changes</label>
            <span className="note">{editedCount} passage{editedCount === 1 ? '' : 's'} edited
              {showChanges ? <> · <ins>Added or changed text</ins> · <del>Original text</del></> : ' · hidden'}
              {approved && ' · approved version'}</span></div>}
          {audioUrl && t && <p className="note">Press play to follow along. Click any word to jump the audio to it.</p>}
          {offers.map(x => <div key={x.from + '|' + x.to} className="banner" role="status">
            You changed “{x.from}” to “{x.to}”. The same words appear {x.count} more time{x.count === 1 ? '' : 's'} in this transcript.
            <div className="actions" style={{ marginTop: 6 }}>
              <button className="primary" onClick={() => replaceAll(x.from, x.to)}>Change {x.count === 1 ? 'it' : `all ${x.count}`}</button>
              <button onClick={() => setOffers(v => v.filter(y => y !== x))}>Leave them</button>
              <span className="note">Tip: add “{x.to}” on the <a href="#/vocabulary">Vocabulary</a> page so it is heard right next time.</span></div></div>)}
          {dictate && t && <DocumentView d={d} t={t} timed={timed} pos={pos} playing={playing} canSeek={!!audioUrl} onSeek={seek}
            editable={d.canEditTranscript} showChanges={showChanges}
            onSave={async edits => { for (const [sid, text] of edits) await api.put(`/items/${id}/segments/${sid}`, { text }); offerSwaps(edits); }} onSaved={load} onError={setError}
            onJoin={sid => run(() => api.post(`/items/${id}/segments/${sid}/join`))} />}
          {!dictate && t?.segments.map((s, i) => (s.text || showChanges) && <SegmentRow
            canMerge={d.canEditTranscript && !!s.text && t.segments.slice(0, i).some(x => x.text)}
            onMerge={() => run(() => api.post(`/items/${id}/segments/${s.id}/merge`))} key={s.id} s={s} words={timed[i] ?? []} active={pos.seg === i} activeWord={pos.seg === i ? pos.word : -1}
            playing={playing} canSeek={!!audioUrl} onSeek={seek} editable={d.canEditTranscript} hideSpeaker={dictate}
            showChanges={showChanges}
            onSave={text => run(async () => { await api.put(`/items/${id}/segments/${s.id}`, { text }); offerSwaps([[s.id, text]]); })} />)}
          {!d.canEditTranscript && t && <p className="note">This transcript is read-only for you right now.</p>}
        </div>
        <div>
          {t && dictate && d.canEditTranscript && d.processingState !== 'Processing' && <GrammarCard itemId={id} onSaved={load} />}
          {t && !dictate && (o || d.canEditTranscript) && <div className="card"><h2>Summary</h2>
            {!o && <p className="note">{d.processingState === 'Processing' ? 'Not ready yet.' : 'No summary yet.'}</p>}
            {o && <>
              <p>{o.summary}</p>
              <h3>Action points</h3>
              {o.actionPoints.length ? <ul>{o.actionPoints.map((a, i) => <li key={i}>{a}</li>)}</ul> : <p className="note">No actions were agreed.</p>}
              <h3>Minutes</h3><div className="minutes">{o.minutes}</div>
              <h3>Tone</h3><p>{o.toneOfMeeting}</p>
              <p className="note">Generated by {o.engine}. Check before relying on it.</p></>}
            {d.canEditTranscript && d.processingState !== 'Processing' && <div className="actions">
              <button disabled={busy} onClick={() => { setBusy(true); run(() => api.post(`/items/${id}/outputs/regenerate`)).finally(() => setBusy(false)); }}>
                {busy ? 'Writing…' : o ? 'Regenerate from current transcript' : 'Create summary'}</button></div>}
          </div>}
          {acts.has('rerun') && t && d.processingState !== 'Processing' && <div className="card"><h2>{dictate ? 'Language looks wrong?' : 'Speakers or language look wrong?'}</h2>
            {dictate ? <p className="note">Convert the dictation again on the same recording, for example with a different language.{corrections > 0 && <> <strong>The text has {corrections} saved correction{corrections > 1 ? 's' : ''}, and converting again replaces them.</strong></>} It is not possible once the dictation has been approved.</p>
            : <p className="note">Run the conversion again on the same recording, for example with a different language. If you know how many people spoke, say so and the app will group the voices into exactly that many. To compare, you can also have the speech service hear the original recording without the audio clean-up.{corrections > 0 && <> <strong>This transcript has {corrections} saved correction{corrections > 1 ? 's' : ''}, and running again replaces them with a fresh transcript.</strong></>} It is not possible once the item has been approved.</p>}
            <div className="actions">
              {!dictate && <><label className="note">Number of speakers{' '}
                <select value={rerunSpeakers} onChange={e => setRerunSpeakers(e.target.value)}>
                  <option value="">Work it out automatically</option>
                  {[2, 3, 4, 5, 6, 7, 8, 9, 10].map(n => <option key={n} value={n}>{n}</option>)}</select></label>
              <label className="note">Way of telling voices apart{' '}
                <select value={rerunMethod} onChange={e => setRerunMethod(e.target.value)}>
                  <option value="">Standard</option>
                  <option value="windowed">Short-window (try this if voices are mixed up)</option>
                  <option value="azure">Speech service only (uses the number of speakers you pick)</option></select></label></>}
              <LanguagePicker value={rerunLang || d.spokenLanguages} onChange={setRerunLang} />
              {dictate && <label className="note">Speech service{' '}
                <select value={rerunService} onChange={e => setRerunService(e.target.value)}>
                  <option value="">Word for word (hears your spoken commands; slower)</option>
                  <option value="fast">Fast (quicker; may drop "new paragraph" and other commands)</option></select></label>}
              <label className="note">Audio to use{' '}
                <select value={rerunAudio} onChange={e => setRerunAudio(e.target.value)}>
                  <option value="">Cleaned up (normal)</option>
                  <option value="original">Original recording, no clean-up (to compare)</option></select></label>
              <button disabled={busy} onClick={() => {
                const edited = corrections > 0;
                if (edited && !confirm(`This replaces your ${corrections} correction${corrections > 1 ? 's' : ''} with a fresh transcript. The history keeps a record that this happened. Continue?`)) return;
                setBusy(true);
                run(() => api.post(`/items/${id}/rerun`, { numSpeakers: rerunSpeakers ? Number(rerunSpeakers) : null, discardEdits: edited, method: rerunMethod || null, audio: rerunAudio || null, spokenLanguages: rerunLang || null, dictationService: dictate ? rerunService || null : null })).finally(() => setBusy(false));
              }}>Run again</button></div></div>}
          {t && <div className="card"><h2>Versions</h2>
            {[...t.versions].reverse().map(v => <div key={v.no} className="note" style={{ marginBottom: 6 }}>
              <b>v{v.no}</b> {v.kind === 'AutoTidy' ? 'Restarted sentences crossed out' : v.kind} · {v.by ?? 'system'} · {new Date(v.at).toLocaleString('en-GB')}
              {d.canEditTranscript && v.no !== t.versionNo && <> <button onClick={() => run(() => api.post(`/items/${id}/versions/${v.no}/restore`))}>Restore</button></>}</div>)}</div>}
          <div className="card"><h2>History</h2>
            <button onClick={() => setShowAudit(!showAudit)}>{showAudit ? 'Hide' : 'Show'} audit trail ({audit.length})</button>
            {showAudit && <ul className="note">{audit.map(a => <li key={a.seq}>{new Date(a.at).toLocaleString('en-GB')} — {describeAudit(a)} · {users.find(u => u.id === a.actorId)?.name ?? 'system'}</li>)}</ul>}
            {d.originalSha256 && <p className="note">Original file fingerprint (SHA-256): {d.originalSha256.slice(0, 16)}…</p>}</div>
        </div>
      </div>
      </div>
    </EditPause.Provider>
  );
}

type WordT = { text: string; startMs: number; endMs: number };

function SegmentRow({ s, words, active, activeWord, playing, canSeek, onSeek, editable, onSave, showChanges, hideSpeaker, canMerge, onMerge }: {
  s: Transcript['segments'][number]; words: WordT[]; active: boolean; activeWord: number;
  playing: boolean; canSeek: boolean; onSeek: (ms: number) => void; editable: boolean; onSave: (t: string) => void;
  showChanges: boolean; hideSpeaker?: boolean; canMerge?: boolean; onMerge?: () => void;
}) {
  const [editing, setEditing] = useState(false);
  const edit = useContext(EditPause);
  const [text, setText] = useState(s.text);
  const fit = useFit(editing ? text : '');
  const row = useRef<HTMLDivElement>(null);
  useEffect(() => setText(s.text), [s.text]);
  // Keep the passage being read in view while playing, without fighting the reader once they pause.
  useEffect(() => {
    if (active && playing) {
      const calm = window.matchMedia('(prefers-reduced-motion: reduce)').matches;
      row.current?.scrollIntoView({ block: 'center', behavior: calm ? 'auto' : 'smooth' });
    }
  }, [active, playing]);

  const word = (i: number) => {
    const w = words[i];
    if (!w) return null;
    return (
      <span className={'w' + (i === activeWord ? ' now' : '')} role={canSeek ? 'button' : undefined} tabIndex={canSeek ? 0 : undefined}
        onClick={canSeek ? () => onSeek(w.startMs) : undefined}
        onKeyDown={canSeek ? e => { if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); onSeek(w.startMs); } } : undefined}>{w.text}</span>
    );
  };
  const marked = showChanges && s.originalText !== null;
  const ops = marked ? diffWords(s.originalText!, s.text) : null;

  return (
    <div ref={row} className={'seg' + (s.lowConfidence ? ' low' : '') + (active ? ' reading' : '') + (marked ? ' changed' : '')} aria-current={active ? 'true' : undefined}>
      <div className="who">{!hideSpeaker && s.speaker}
        <div className="t">{canSeek ? <button className="link" onClick={() => onSeek(s.startMs)} title="Play from here">{fmtLen(s.startMs)}</button> : fmtLen(s.startMs)} · {s.language}
          {marked && <> · <span className="tag">Edited</span></>}</div></div>
      <div>
        {editing ? <>
          <textarea ref={fit} className="fit" rows={3} value={text} autoFocus onChange={e => setText(e.target.value)} />
          <div className="actions"><button className="primary" disabled={text.trim() === s.text}
            onClick={() => { onSave(text); setEditing(false); edit.resume(); }}>Save</button>
            <button onClick={() => { setText(s.text); setEditing(false); edit.resume(); }}>Cancel</button></div>
        </> : <>
          <p className="words">{ops
            ? ops.map((o, i) => o.kind === 'del' ? <span key={i}><del>{o.text}</del>{' '}</span>
              : o.kind === 'ins' ? <span key={i}><ins>{word(o.cur!)}</ins>{' '}</span>
              : <span key={i}>{word(o.cur!)}{' '}</span>)
            : words.map((_, i) => <span key={i}>{word(i)}{' '}</span>)}</p>
          {editable && <div className="actions"><button onClick={() => { edit.pause(); setEditing(true); }}>Edit</button>
            {canMerge && onMerge && <button onClick={onMerge} title="These words move into the box above, with that box's speaker">Join to box above</button>}</div>}
        </>}
        {s.lowConfidence && <div className="note">Low confidence — please check.</div>}
      </div>
    </div>
  );
}

const longDate = (iso: string) => new Date(iso).toLocaleDateString('en-GB', { day: 'numeric', month: 'long', year: 'numeric' });

/** A dictation shown as the letter, Word document or email the person said it was. */
function DocumentView({ d, t, timed, pos, playing, canSeek, onSeek, editable, showChanges, onSave, onSaved, onError, onJoin }: {
  d: Detail; t: Transcript; timed: WordT[][]; pos: { seg: number; word: number }; playing: boolean; canSeek: boolean;
  onSeek: (ms: number) => void; editable: boolean; showChanges: boolean;
  onSave: (edits: [string, string][]) => Promise<void>; onSaved: () => void; onError: (m: string) => void; onJoin: (segmentId: string) => void;
}) {
  const paras = useMemo(() => paragraphPieces(t.segments), [t]);
  const markers = useMemo(() => paragraphMarkers(t.segments, paras), [t, paras]);
  const type = d.documentType ?? 'document';
  const anyLow = t.segments.some(s => s.lowConfidence);
  return (
    <>
      {anyLow && <p className="note"><span className="low-c-key">Shaded</span> text was hard to hear. Please check it.</p>}
      <article className="page" aria-label={docLabel(d.documentType)}>
        {type === 'letter' && <>
          <div className="page-date">{longDate(d.row.createdOn)}</div>
          <div className="page-ref">{d.clientReference ? <>Our ref: {d.clientReference}</> : null}</div></>}
        {type === 'email' && <div className="mail-head">
          <div><b>Subject</b> {d.row.name}</div>
          {d.clientReference && <div><b>Reference</b> {d.clientReference}</div>}</div>}
        {type === 'document' && <h1 className="page-title">{d.row.name}</h1>}
        {paras.map((pieces, k) => <Paragraph key={`${t.segments[pieces[0].seg].id}-${pieces[0].from}`} pieces={pieces} marker={markers[k]} t={t} timed={timed} pos={pos} playing={playing}
          canSeek={canSeek} onSeek={onSeek} editable={editable} showChanges={showChanges} onSave={onSave} onSaved={onSaved} onError={onError}
          onJoin={k > 0 && pieces[0].from === 0 ? () => onJoin(t.segments[pieces[0].seg].id) : undefined} />)}
      </article>
    </>
  );
}

function Paragraph({ pieces, marker, t, timed, pos, playing, canSeek, onSeek, editable, showChanges, onSave, onSaved, onError, onJoin }: {
  pieces: Piece[]; marker: string | null; t: Transcript; timed: WordT[][]; pos: { seg: number; word: number }; playing: boolean; canSeek: boolean;
  onSeek: (ms: number) => void; editable: boolean; showChanges: boolean;
  onSave: (edits: [string, string][]) => Promise<void>; onSaved: () => void; onError: (m: string) => void;
  /** Present when this paragraph starts at a passage that can carry on the paragraph above instead. */
  onJoin?: () => void;
}) {
  // Editing works on whole passages: a passage split across paragraphs shows all of its text, line breaks included.
  const idx = [...new Set(pieces.map(p => p.seg))];
  const segs = idx.map(i => t.segments[i]);
  const [editing, setEditing] = useState(false);
  const edit = useContext(EditPause);
  const [texts, setTexts] = useState(segs.map(s => s.text));
  const [saving, setSaving] = useState(false);
  const el = useRef<HTMLDivElement>(null);
  const reading = pieces.some(p => p.seg === pos.seg && (pos.word < 0 ? p.from === 0 : pos.word >= p.from && pos.word < p.to));
  useEffect(() => { if (!editing) setTexts(segs.map(s => s.text)); }, [t]); // eslint-disable-line react-hooks/exhaustive-deps
  useEffect(() => {
    if (reading && playing) el.current?.scrollIntoView({ block: 'center', behavior: window.matchMedia('(prefers-reduced-motion: reduce)').matches ? 'auto' : 'smooth' });
  }, [reading, playing]);
  // A line break typed at the end of a box belongs to the start of the next passage, so that passage begins a new paragraph.
  const outgoing = () => {
    const v = texts.map(x => x.replace(/\r\n?/g, '\n'));
    for (let k = 0; k < v.length - 1; k++) if (/\n\s*$/.test(v[k]) && v[k].trim()) { v[k] = v[k].replace(/\s+$/, ''); v[k + 1] = '\n' + v[k + 1].replace(/^\s+/, ''); }
    return segs.map((s, k) => [s.id, v[k]] as [string, string])
      // An emptied box is sent too: its words are taken out (they show struck through, and Restore brings them back).
      .filter(([, x], k) => x.trim() !== segs[k].text || (x.trim() !== '' && /^\s*\n/.test(x) && k > 0 && segs[k].newParagraph === false));
  };

  if (editing) return (
    <div className="para" ref={el}>
      <p className="note">Press Enter where a new paragraph should start. Empty a box to take its words out.</p>
      {segs.map((s, k) => <FitArea key={s.id} value={texts[k]}
        label={`Sentence ${k + 1} of this paragraph`} onChange={x => setTexts(v => v.map((y, j) => j === k ? x : y))} />)}
      <div className="actions para-tools"><button className="primary" disabled={saving || outgoing().length === 0}
        onClick={async () => { setSaving(true); try { await onSave(outgoing()); setEditing(false); onSaved(); edit.resume(); } catch (e: any) { onError(e.message); } finally { setSaving(false); } }}>
        {saving ? 'Saving…' : 'Save'}</button>
        <button onClick={() => { setTexts(segs.map(s => s.text)); setEditing(false); edit.resume(); }}>Cancel</button></div>
    </div>);

  return (
    <div className={'para' + (reading ? ' reading' : '') + (marker ? ' list-point' : '')} ref={el}>
      {marker && <span className="list-marker" aria-hidden="true">{marker}</span>}
      {pieces.map(p => {
        const s = t.segments[p.seg];
        return <span key={`${s.id}-${p.from}`} className={s.lowConfidence ? 'low-c' : undefined} title={s.lowConfidence ? 'Hard to hear. Please check.' : undefined}>
          <Words s={s} words={timed[p.seg] ?? []} from={p.from} to={p.to} activeWord={pos.seg === p.seg ? pos.word : -1} canSeek={canSeek} onSeek={onSeek} showChanges={showChanges} />{' '}</span>;
      })}
      {editable && <button className="link para-edit" onClick={() => { edit.pause(); setEditing(true); }}>Edit</button>}
      {editable && onJoin && <button className="link para-edit" onClick={onJoin} title="This paragraph carries on the one above, with no break between them">Join to paragraph above</button>}
    </div>);
}

/** Words [from, to) of a passage, with tracked changes when shown. Removed words appear with the word that follows them. */
function Words({ s, words, from, to, activeWord, canSeek, onSeek, showChanges }: {
  s: Transcript['segments'][number]; words: WordT[]; from: number; to: number; activeWord: number; canSeek: boolean; onSeek: (ms: number) => void; showChanges: boolean;
}) {
  const word = (i: number) => {
    const w = words[i];
    if (!w) return null;
    return <span className={'w' + (i === activeWord ? ' now' : '')} role={canSeek ? 'button' : undefined} tabIndex={canSeek ? 0 : undefined}
      onClick={canSeek ? () => onSeek(w.startMs) : undefined}
      onKeyDown={canSeek ? e => { if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); onSeek(w.startMs); } } : undefined}>{w.text}</span>;
  };
  const all = showChanges && s.originalText !== null ? diffWords(s.originalText, s.text) : null;
  if (!all) return <>{words.slice(from, to).map((_, k) => <span key={k}>{word(from + k)}{from + k < to - 1 ? ' ' : ''}</span>)}</>;
  // Where each removed word is shown: with the next word that is still there (or at the very end).
  const ops = all.filter((o, i) => {
    if (o.cur !== undefined) return o.cur >= from && o.cur < to;
    const next = all.slice(i + 1).find(x => x.cur !== undefined)?.cur;
    return next === undefined ? to === words.length : next >= from && next < to;
  });
  return <>{ops.map((o, i) => o.kind === 'del' ? <span key={i}><del>{o.text}</del>{' '}</span>
    : o.kind === 'ins' ? <span key={i}><ins>{word(o.cur!)}</ins>{i < ops.length - 1 ? ' ' : ''}</span>
    : <span key={i}>{word(o.cur!)}{i < ops.length - 1 ? ' ' : ''}</span>)}</>;
}

/** A dictation passage's edit box, grown to show all its text. */
function FitArea({ value, label, onChange }: { value: string; label: string; onChange: (v: string) => void }) {
  const ref = useFit(value);
  return <textarea ref={ref} className="fit" rows={2} value={value} aria-label={label} onChange={e => onChange(e.target.value)} />;
}
