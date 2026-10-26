import { useEffect, useState } from 'react';
import { api, type ItemRetention } from './api';

/** One line on the item page saying what the retention rules will do to this recording, with Put on hold / Release for administrators. */
export default function RetentionLine({ id }: { id: string }) {
  const [r, setR] = useState<ItemRetention | null>(null);
  const [asking, setAsking] = useState(false);
  const [reason, setReason] = useState('');
  const [error, setError] = useState('');
  const [busy, setBusy] = useState(false);
  useEffect(() => { api.itemRetention(id).then(setR).catch(() => setR(null)); }, [id]);

  const run = async (fn: () => Promise<ItemRetention>) => {
    setBusy(true); setError('');
    try { setR(await fn()); setAsking(false); setReason(''); } catch (e: any) { setError(e.message); } finally { setBusy(false); }
  };
  if (!r || (!r.note && !r.canHold)) return null;
  return (
    <div className="note retention" style={{ marginTop: 8 }}>
      {r.note}{r.held && r.holdReason && <> Why: {r.holdReason}</>}
      {r.canHold && (r.held
        ? <> <button disabled={busy} onClick={() => run(() => api.releaseItem(id))}>Release the hold</button></>
        : !asking && <> <button disabled={busy} onClick={() => setAsking(true)}>Put on hold</button></>)}
      {asking && <div className="actions" style={{ marginTop: 6 }}>
        <input placeholder="Why must it be kept? (for example a complaint)" value={reason} maxLength={500} onChange={e => setReason(e.target.value)} style={{ minWidth: 280 }} />
        <button className="primary" disabled={busy || !reason.trim()} onClick={() => run(() => api.holdItem(id, reason))}>Put on hold</button>
        <button onClick={() => setAsking(false)}>Cancel</button></div>}
      {asking && <div>While on hold, the retention rules never delete it.</div>}
      {error && <div className="error">{error}</div>}
    </div>
  );
}
