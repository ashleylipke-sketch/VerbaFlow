import { useEffect, useState } from 'react';
import { api, fmtDate, saveFile, type AttachmentList, type AttachmentRow } from './api';

const ACCEPT = '.pdf,.docx,.xlsx,.pptx,.doc,.xls,.ppt,.odt,.ods,.odp,.rtf,.txt,.csv,.png,.jpg,.jpeg,.gif,.webp,.msg,.eml';
export const size = (b: number) => b < 1024 * 1024 ? `${Math.max(1, Math.round(b / 1024))} KB` : `${(b / 1024 / 1024).toFixed(1)} MB`;

/** Files kept with a recording: agenda, papers, slides. Added and removed until it is approved. */
export default function AttachmentsCard({ id, onChange }: { id: string; onChange?: () => void }) {
  const [l, setL] = useState<AttachmentList | null>(null);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState('');
  const [confirming, setConfirming] = useState<string | null>(null);
  useEffect(() => { api.attachments(id).then(setL).catch(e => setError(e.message)); }, [id]);

  const run = async (fn: () => Promise<AttachmentList | void>) => {
    setBusy(true); setError('');
    try { const x = await fn(); if (x) { setL(x); onChange?.(); } } catch (e: any) { setError(e.message); } finally { setBusy(false); setConfirming(null); }
  };
  const open = (f: AttachmentRow) => run(async () => {
    const blob = await api.attachmentFile(id, f.id);
    // PDFs and pictures open in a new tab; anything else is downloaded.
    if (f.contentType === 'application/pdf' || f.contentType.startsWith('image/')) window.open(URL.createObjectURL(blob), '_blank');
    else saveFile(new File([blob], f.fileName));
  });
  if (!l) return error ? <div className="card"><h2>Attached files</h2><div className="error">{error}</div></div> : null;
  if (l.files.length === 0 && !l.canAdd) return null;

  return (
    <div className="card attachments">
      <h2>Attached files</h2>
      {l.files.length === 0 && <p className="note">None yet. Add the agenda, papers or anything else that belongs with this recording.</p>}
      {l.files.map(f => (
        <div key={f.id} className="attachment">
          <button className="link" disabled={busy} onClick={() => open(f)} title="Open or download">{f.fileName}</button>
          <div className="note">{size(f.sizeBytes)} · {f.addedBy} · {fmtDate(f.addedAt)}
            {f.canRemove && confirming !== f.id && <> · <button className="link" disabled={busy} onClick={() => setConfirming(f.id)}>Remove</button></>}
            {confirming === f.id && <> · Remove it? <button className="link danger" onClick={() => run(() => api.removeAttachment(id, f.id))}>Yes</button> <button className="link" onClick={() => setConfirming(null)}>No</button></>}</div>
        </div>))}
      {l.canAdd && <div className="actions" style={{ marginTop: 8 }}>
        <label className="btn">{busy ? 'Adding…' : 'Add files…'}
          <input type="file" hidden multiple accept={ACCEPT} disabled={busy}
            onChange={e => { const fs = [...(e.target.files ?? [])]; e.target.value = ''; if (fs.length) run(() => api.addAttachments(id, fs)); }} /></label>
      </div>}
      {l.canAdd && <p className="note">PDF, Word, Excel, PowerPoint, text, pictures or emails, up to 25 MB each. Locked once approved.</p>}
      {!l.canAdd && l.why && l.files.length > 0 && <p className="note">{l.why}</p>}
      {error && <div className="error">{error}</div>}
    </div>
  );
}
