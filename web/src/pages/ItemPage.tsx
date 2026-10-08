import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { api, fmtDate, fmtLen, type AuditEvent, type Detail, type Outputs, type Transcript, type UserView } from '../api';
import { activeSegmentIndex, activeWordIndex, wordsFor } from '../words';
import { diffWords } from '../diff';

export default function ItemPage({ id }: { id: string }) {
  const [d, setD] = useState<Detail | null>(null);
  const [t, setT] = useState<Transcript | null>(null);
  const [o, setO] = useState<Outputs | null>(null);
  const [audit, setAudit] = useState<AuditEvent[]>([]);
  const [users, setUsers] = useState<UserView[]>([]);
  const [audioUrl, setAudioUrl] = useState('');
  const [error, setError] = useState('');
  const [assignTo, setAssignTo] = useState('');
  const [reason, setReason] = useState('');
  const [reopenReason, setReopenReason] = useState('');
  const [showAudit, setShowAudit] = useState(false);
  const player = useRef<HTMLAudioElement>(null);
  const [pos, setPos] = useState({ seg: -1, word: -1 });
  const [playing, setPlaying] = useState(false);
  // Tracked changes show while the transcript is a draft and are tucked away once it is approved. The reader can override either way.
  const [changesChoice, setChangesChoice] = useState<boolean | null>(null);

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
  useEffect(() => {
    let url = '';
    api.audio(id).then(b => { url = URL.createObjectURL(b); setAudioUrl(url); }).catch(() => setAudioUrl(''));
    return () => { if (url) URL.revokeObjectURL(url); };
  }, [id]);

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
  const labelOf = (speakerId: string) => t?.speakers.find(x => x.id === speakerId)?.label ?? '';
  const editedCount = segs.filter(x => x.originalText !== null).length;
  const renamedCount = (t?.speakers ?? []).filter(x => x.name !== x.label).length;
  const seek = (ms: number) => { const a = player.current; if (!a) return; a.currentTime = ms / 1000; a.play().catch(() => undefined); };

  const run = async (fn: () => Promise<unknown>) => { setError(''); try { await fn(); await load(); } catch (e: any) { setError(e.message); } };
  const download = () => run(async () => { const b = await api.audio(id, true); const a = document.createElement('a'); a.href = URL.createObjectURL(b); a.download = d!.originalFileName ?? 'audio'; a.click(); });

  if (!d) return <div className="card">{error ? <span className="error">{error}</span> : 'Loading…'}</div>;
  const r = d.row, acts = new Set(r.actions);
  const assignee = users.filter(u => u.id !== undefined);

  return (
    <>
      <p><a href="#/">← Back to Meeting</a></p>
      <div className="card">
        <h2>{r.name} {r.tags.map(x => <span key={x} className="tag">{x}</span>)}</h2>
        <div className="note">
          <span className="pill">{r.status}</span> · {fmtLen(r.lengthMs)} · Owner {d.owner} · You are: {d.role} · Created {fmtDate(r.createdOn)}
          {r.assignedTo && <> · Assigned to {r.assignedTo}</>}
          {d.approvedAt && <> · Approved by {d.approvedBy} on {fmtDate(d.approvedAt)}</>}
        </div>
        {d.processingState === 'Processing' && <div className="banner">Transcribing… this page updates itself when it is ready.</div>}
        {d.failureReason && <div className="banner">Conversion failed: {d.failureReason}</div>}
        {d.markers.length > 0 && <div className="note">Markers: {d.markers.map(m => `${fmtLen(m.offsetMs)} ${m.type}${m.note ? ` (${m.note})` : ''}`).join(' · ')}</div>}
        {d.chain.length > 1 && <div className="note">Versions: {d.chain.map(c => <span key={c.id}>{c.isCurrent ? <b>v{c.versionNo}</b> : <a href={`#/items/${c.id}`}>v{c.versionNo}</a>} ({c.status}){' '}</span>)}</div>}
        {audioUrl && <div className="player"><audio ref={player} controls src={audioUrl} onLoadedMetadata={fixDuration} style={{ width: '100%' }} />
          {d.canDownloadAudio && <button onClick={download}>Download original audio</button>}</div>}
        {error && <div className="error">{error}</div>}
        <div className="actions" style={{ marginTop: 12, flexWrap: 'wrap' }}>
          {acts.has('retry') && <button onClick={() => run(() => api.post(`/items/${id}/retry`))}>Retry conversion</button>}
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
      </div>

      <div className="split">
        <div className="card">
          <h2>Transcript {t && <span className="note">version {t.versionNo} · {t.engine}</span>}</h2>
          {!t && <p className="note">{d.processingState === 'Processing' ? 'Not ready yet.' : 'No transcript.'}</p>}
          {t && <p className="note">Speakers: {t.speakers.map(s => (
            <span key={s.id} style={{ marginRight: 10 }}>{s.name}{d.canEditTranscript && <> <button onClick={() => { const n = prompt('Rename speaker', s.name); if (n?.trim()) run(() => api.put(`/items/${id}/speakers/${s.id}`, { name: n.trim() })); }}>Rename</button></>}</span>))}</p>}
          {t && (editedCount > 0 || renamedCount > 0) && <div className="changes-bar">
            <label><input type="checkbox" checked={showChanges} onChange={e => setChangesChoice(e.target.checked)} /> Show tracked changes</label>
            <span className="note">{editedCount} passage{editedCount === 1 ? '' : 's'} edited · {renamedCount} speaker{renamedCount === 1 ? '' : 's'} renamed
              {showChanges ? <> · <ins>Added or changed text</ins> · <del>Original text</del></> : ' · hidden'}
              {approved && ' · approved version'}</span></div>}
          {audioUrl && t && <p className="note">Press play to follow along. Click any word to jump the audio to it.</p>}
          {t?.segments.map((s, i) => <SegmentRow key={s.id} s={s} words={timed[i] ?? []} active={pos.seg === i} activeWord={pos.seg === i ? pos.word : -1}
            playing={playing} canSeek={!!audioUrl} onSeek={seek} editable={d.canEditTranscript}
            showChanges={showChanges} machineLabel={labelOf(s.speakerId)}
            onSave={text => run(() => api.put(`/items/${id}/segments/${s.id}`, { text }))} />)}
          {!d.canEditTranscript && t && <p className="note">This transcript is read-only for you right now.</p>}
        </div>
        <div>
          {o && <div className="card"><h2>Summary</h2><p>{o.summary}</p>
            <h3>Action points</h3><ul>{o.actionPoints.map((a, i) => <li key={i}>{a}</li>)}</ul>
            <p className="note">Generated by {o.engine}. Check before relying on it.</p></div>}
          {t && <div className="card"><h2>Versions</h2>
            {[...t.versions].reverse().map(v => <div key={v.no} className="note" style={{ marginBottom: 6 }}>
              <b>v{v.no}</b> {v.kind} · {v.by ?? 'system'} · {new Date(v.at).toLocaleString('en-GB')}
              {d.canEditTranscript && v.no !== t.versionNo && <> <button onClick={() => run(() => api.post(`/items/${id}/versions/${v.no}/restore`))}>Restore</button></>}</div>)}</div>}
          <div className="card"><h2>History</h2>
            <button onClick={() => setShowAudit(!showAudit)}>{showAudit ? 'Hide' : 'Show'} audit trail ({audit.length})</button>
            {showAudit && <ul className="note">{audit.map(a => <li key={a.seq}>{new Date(a.at).toLocaleString('en-GB')} — {a.action} by {a.actor}</li>)}</ul>}
            {d.originalSha256 && <p className="note">Original file fingerprint (SHA-256): {d.originalSha256.slice(0, 16)}…</p>}</div>
        </div>
      </div>
    </>
  );
}

type WordT = { text: string; startMs: number; endMs: number };

function SegmentRow({ s, words, active, activeWord, playing, canSeek, onSeek, editable, onSave, showChanges, machineLabel }: {
  s: Transcript['segments'][number]; words: WordT[]; active: boolean; activeWord: number;
  playing: boolean; canSeek: boolean; onSeek: (ms: number) => void; editable: boolean; onSave: (t: string) => void;
  showChanges: boolean; machineLabel: string;
}) {
  const [editing, setEditing] = useState(false);
  const [text, setText] = useState(s.text);
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
  const renamed = showChanges && machineLabel !== '' && machineLabel !== s.speaker;

  return (
    <div ref={row} className={'seg' + (s.lowConfidence ? ' low' : '') + (active ? ' reading' : '') + (marked || renamed ? ' changed' : '')} aria-current={active ? 'true' : undefined}>
      <div className="who">{renamed ? <><del>{machineLabel}</del> <ins>{s.speaker}</ins></> : s.speaker}
        <div className="t">{canSeek ? <button className="link" onClick={() => onSeek(s.startMs)} title="Play from here">{fmtLen(s.startMs)}</button> : fmtLen(s.startMs)} · {s.language}
          {marked && <> · <span className="tag">Edited</span></>}</div></div>
      <div>
        {editing ? <>
          <textarea rows={3} value={text} autoFocus onChange={e => setText(e.target.value)} />
          <div className="actions"><button className="primary" disabled={!text.trim() || text.trim() === s.text}
            onClick={() => { onSave(text); setEditing(false); }}>Save</button>
            <button onClick={() => { setText(s.text); setEditing(false); }}>Cancel</button></div>
        </> : <>
          <p className="words">{ops
            ? ops.map((o, i) => o.kind === 'del' ? <span key={i}><del>{o.text}</del>{' '}</span>
              : o.kind === 'ins' ? <span key={i}><ins>{word(o.cur!)}</ins>{' '}</span>
              : <span key={i}>{word(o.cur!)}{' '}</span>)
            : words.map((_, i) => <span key={i}>{word(i)}{' '}</span>)}</p>
          {editable && <button onClick={() => setEditing(true)}>Edit</button>}
        </>}
        {s.lowConfidence && <div className="note">Low confidence — please check.</div>}
      </div>
    </div>
  );
}
