import { useCallback, useEffect, useState } from 'react';
import { api, fmtDate, fmtLen, type AuditEvent, type Detail, type Outputs, type Transcript, type UserView } from '../api';

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

  const load = useCallback(async () => {
    try {
      const det = await api.item(id); setD(det);
      api.users().then(setUsers);
      api.transcript(id).then(setT).catch(() => setT(null));
      api.outputs(id).then(setO).catch(() => setO(null));
      api.audit(id).then(setAudit).catch(() => setAudit([]));
    } catch (e: any) { setError(e.message); }
  }, [id]);
  useEffect(() => { load(); const i = setInterval(() => { if (d?.processingState === 'Running') load(); }, 3000); return () => clearInterval(i); }, [load, d?.processingState]);
  useEffect(() => {
    let url = '';
    api.audio(id).then(b => { url = URL.createObjectURL(b); setAudioUrl(url); }).catch(() => setAudioUrl(''));
    return () => { if (url) URL.revokeObjectURL(url); };
  }, [id]);

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
        {d.processingState === 'Running' && <div className="banner">Transcribing… this page updates itself when it is ready.</div>}
        {d.failureReason && <div className="banner">Conversion failed: {d.failureReason}</div>}
        {d.markers.length > 0 && <div className="note">Markers: {d.markers.map(m => `${fmtLen(m.offsetMs)} ${m.type}${m.note ? ` (${m.note})` : ''}`).join(' · ')}</div>}
        {d.chain.length > 1 && <div className="note">Versions: {d.chain.map(c => <span key={c.id}>{c.isCurrent ? <b>v{c.versionNo}</b> : <a href={`#/items/${c.id}`}>v{c.versionNo}</a>} ({c.status}){' '}</span>)}</div>}
        {audioUrl && <div style={{ marginTop: 10 }}><audio controls src={audioUrl} style={{ width: '100%' }} />
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
          {!t && <p className="note">{d.processingState === 'Running' ? 'Not ready yet.' : 'No transcript.'}</p>}
          {t && <p className="note">Speakers: {t.speakers.map(s => (
            <span key={s.id} style={{ marginRight: 10 }}>{s.name}{d.canEditTranscript && <> <button onClick={() => { const n = prompt('Rename speaker', s.name); if (n?.trim()) run(() => api.put(`/items/${id}/speakers/${s.id}`, { name: n.trim() })); }}>Rename</button></>}</span>))}</p>}
          {t?.segments.map(s => <SegmentRow key={s.id} s={s} editable={d.canEditTranscript} onSave={text => run(() => api.put(`/items/${id}/segments/${s.id}`, { text }))} />)}
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

function SegmentRow({ s, editable, onSave }: { s: Transcript['segments'][number]; editable: boolean; onSave: (t: string) => void }) {
  const [text, setText] = useState(s.text);
  useEffect(() => setText(s.text), [s.text]);
  return (
    <div className={'seg' + (s.lowConfidence ? ' low' : '')}>
      <div className="who">{s.speaker}<div className="t">{fmtLen(s.startMs)} · {s.language}</div></div>
      <div>{editable ? <><textarea rows={2} value={text} onChange={e => setText(e.target.value)} />
        {text !== s.text && <button className="primary" onClick={() => onSave(text)}>Save</button>}</> : s.text}
        {s.lowConfidence && <div className="note">Low confidence — please check.</div>}</div>
    </div>
  );
}
