import { useState } from 'react';
import { api } from './api';
import { changes, compose, hunks, type Hunk } from './grammar';

type Suggestion = { segmentId: string; text: string; suggested: string; reason: string };
type Check = { engine: string; passagesChecked: number; suggestions: Suggestion[]; leftOut: number };
/** One passage's suggestion while the person works through it: `current` is its text as now saved. */
type Row = { segmentId: string; current: string; reason: string; hs: Hunk[]; accepted: Set<number>; rejected: Set<number> };

/**
 * Check grammar (owner chose "Suggest, I accept"): the AI suggests corrections, shown change by change. Nothing is
 * changed until the person accepts a change; each accepted change is saved as a tracked edit.
 */
export default function GrammarCard({ itemId, onSaved }: { itemId: string; onSaved: () => void }) {
  const [rows, setRows] = useState<Row[] | null>(null);
  const [leftOut, setLeftOut] = useState(0);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState('');

  const check = async () => {
    setBusy(true); setError('');
    try {
      const r: Check = await api.post(`/items/${itemId}/grammar`);
      setLeftOut(r.leftOut);
      setRows(r.suggestions.map(s => ({ segmentId: s.segmentId, current: s.text, reason: s.reason, hs: hunks(s.text, s.suggested), accepted: new Set(), rejected: new Set() })));
    } catch (e: any) { setError(e.message); } finally { setBusy(false); }
  };

  const update = (i: number, row: Row) => setRows(rs => rs && rs.map((r, k) => k === i ? row : r));

  const accept = async (i: number, ks: number[]) => {
    const row = rows![i];
    const accepted = new Set([...row.accepted, ...ks]);
    setBusy(true); setError('');
    try {
      const r = await api.post(`/items/${itemId}/grammar/apply`, { segmentId: row.segmentId, expected: row.current, text: compose(row.hs, accepted) });
      update(i, { ...row, segmentId: r.segmentId, current: r.text, accepted });
      onSaved();
    } catch (e: any) { setError(e.message); } finally { setBusy(false); }
  };
  const reject = (i: number, ks: number[]) => { const row = rows![i]; update(i, { ...row, rejected: new Set([...row.rejected, ...ks]) }); };

  const open = (r: Row) => changes(r.hs).filter(k => !r.accepted.has(k) && !r.rejected.has(k));
  const left = rows?.filter(r => open(r).length > 0) ?? [];
  const pending = rows?.reduce((n, r) => n + open(r).length, 0) ?? 0;

  const acceptAll = async () => { for (let i = 0; i < (rows?.length ?? 0); i++) { const o = open(rows![i]); if (o.length) await accept(i, o); } };

  return (
    <div className="card grammar">
      <h2>Grammar and punctuation</h2>
      <p className="note">Suggestions only: nothing changes until you accept it. Each change you accept is saved as a tracked change.</p>
      <div className="actions">
        <button className="primary" disabled={busy} onClick={check}>{busy && !rows ? 'Checking…' : rows ? 'Check again' : 'Check grammar'}</button>
        {pending > 1 && <button disabled={busy} onClick={acceptAll}>Accept all ({pending})</button>}
        {pending > 1 && <button disabled={busy} onClick={() => rows!.forEach((r, i) => reject(i, open(r)))}>Reject all</button>}
      </div>
      {rows && rows.length === 0 && <p className="ok" role="status">No suggestions: the grammar and punctuation look right.</p>}
      {rows && rows.length > 0 && left.length === 0 && <p className="ok" role="status">All suggestions dealt with.</p>}
      {leftOut > 0 && <p className="note">{leftOut} suggestion{leftOut === 1 ? ' was' : 's were'} left out because {leftOut === 1 ? 'it' : 'they'} rewrote too much of the wording.</p>}
      {rows?.map((r, i) => open(r).length > 0 && <div key={i} className="suggestion">
        <div className="note">{r.reason}</div>
        <p>{r.hs.map((h, k) => h.same ? <span key={k}>{h.removed.join(' ')} </span>
          : r.accepted.has(k) ? <span key={k}>{h.added.join(' ')} </span>
          : r.rejected.has(k) ? <span key={k}>{h.removed.join(' ')} </span>
          : <span key={k} className="change">
              {h.removed.length > 0 && <del>{h.removed.join(' ')}</del>}{h.removed.length > 0 && h.added.length > 0 && ' '}
              {h.added.length > 0 && <ins>{h.added.join(' ')}</ins>}
              <button className="small" disabled={busy} aria-label={`Accept: ${h.added.join(' ') || 'remove ' + h.removed.join(' ')}`} onClick={() => accept(i, [k])}>✓</button>
              <button className="small" disabled={busy} aria-label={`Reject: keep ${h.removed.join(' ') || 'as it is'}`} onClick={() => reject(i, [k])}>✗</button>{' '}
            </span>)}</p>
        {open(r).length > 1 && <div className="actions"><button disabled={busy} onClick={() => accept(i, open(r))}>Accept this passage</button>
          <button disabled={busy} onClick={() => reject(i, open(r))}>Reject this passage</button></div>}
      </div>)}
      {error && <div className="error">{error}</div>}
    </div>
  );
}
