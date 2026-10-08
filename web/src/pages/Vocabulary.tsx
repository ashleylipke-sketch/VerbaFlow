import { useEffect, useState } from 'react';
import { api, type Term, type UserView } from '../api';

const MAX_TERMS = 2000;

export default function Vocabulary() {
  const [terms, setTerms] = useState<Term[] | null>(null);
  const [me, setMe] = useState<UserView | null>(null);
  const [search, setSearch] = useState('');
  const [text, setText] = useState('');
  const [note, setNote] = useState('');
  const [bulk, setBulk] = useState('');
  const [showBulk, setShowBulk] = useState(false);
  const [skipped, setSkipped] = useState<string[]>([]);
  const [message, setMessage] = useState('');
  const [error, setError] = useState('');
  const [confirming, setConfirming] = useState<string | null>(null);

  const load = () => api.vocabulary().then(setTerms).catch(e => setError(e.message));
  useEffect(() => { load(); api.me().then(setMe); }, []);
  const admin = me?.isAdmin === true;

  const run = async (fn: () => Promise<unknown>) => { setError(''); setMessage(''); try { await fn(); await load(); } catch (e: any) { setError(e.message); } };
  const add = () => run(async () => { const t = await api.addTerm(text, note); setText(''); setNote(''); setMessage(`Added "${t.text}".`); });
  const doImport = () => run(async () => {
    const r = await api.importTerms(bulk);
    setSkipped(r.skipped); setMessage(`Added ${r.added} term${r.added === 1 ? '' : 's'}${r.skipped.length ? `, skipped ${r.skipped.length}` : ''}.`);
    if (r.skipped.length === 0) setBulk('');
  });
  const remove = (t: Term) => run(async () => { await api.removeTerm(t.id); setConfirming(null); setMessage(`Removed "${t.text}".`); });

  const shown = (terms ?? []).filter(t => !search || `${t.text} ${t.note ?? ''}`.toLowerCase().includes(search.toLowerCase()));

  return (
    <div className="card">
      <h2>Custom vocabulary</h2>
      <p className="note">Names and terms the speech service should expect: people, clients, products, places and jargon. It makes them more likely to be
        transcribed correctly, but it can't force a word, so you should still check transcripts. Changes apply to recordings transcribed from now on, not to ones already done.
        Keep to what you actually need: the service works best with a short, focused list.</p>
      {error && <div className="error">{error}</div>}
      {message && <div className="ok" role="status">{message}</div>}

      {admin && <>
        <div className="actions" style={{ margin: '12px 0', alignItems: 'end' }}>
          <label className="field">Name or term<input value={text} maxLength={100} onChange={e => setText(e.target.value)} onKeyDown={e => e.key === 'Enter' && text.trim() && add()} placeholder="e.g. Bruno Fernandes" /></label>
          <label className="field">Note (optional, for your team)<input value={note} maxLength={200} onChange={e => setNote(e.target.value)} placeholder="e.g. Client contact" style={{ minWidth: 240 }} /></label>
          <button className="primary" disabled={!text.trim()} onClick={add}>Add</button>
          <button onClick={() => setShowBulk(!showBulk)}>{showBulk ? 'Hide bulk add' : 'Add many at once'}</button>
        </div>
        {showBulk && <div className="field">
          <label>One name or term per line
            <textarea rows={6} value={bulk} onChange={e => setBulk(e.target.value)} placeholder={'Amarin\nBruno Fernandes\nOld Trafford'} /></label>
          <div className="actions"><button className="primary" disabled={!bulk.trim()} onClick={doImport}>Add these</button></div>
          {skipped.length > 0 && <details open><summary className="note">{skipped.length} line{skipped.length === 1 ? '' : 's'} skipped</summary>
            <ul className="note">{skipped.map((s, i) => <li key={i}>{s}</li>)}</ul></details>}
        </div>}
      </>}
      {me && !admin && <p className="note">Only administrators can change the vocabulary. Ask one to add anything that is missing.</p>}

      <div className="actions" style={{ margin: '12px 0', alignItems: 'center' }}>
        <input placeholder="Search the vocabulary" value={search} onChange={e => setSearch(e.target.value)} />
        <span className="note">{terms === null ? '' : `${terms.length} of ${MAX_TERMS} terms${search ? ` · ${shown.length} shown` : ''}`}</span>
      </div>
      <div className="scroll">
        <table>
          <thead><tr><th>Term</th><th>Note</th><th>Added</th>{admin && <th>Actions</th>}</tr></thead>
          <tbody>
            {terms === null && <tr><td colSpan={4}>Loading…</td></tr>}
            {terms !== null && terms.length === 0 && <tr><td colSpan={4} className="note">The vocabulary is empty.{admin ? ' Add the first term above.' : ''}</td></tr>}
            {terms !== null && terms.length > 0 && shown.length === 0 && <tr><td colSpan={4} className="note">Nothing matches your search.</td></tr>}
            {shown.map(t => (
              <tr key={t.id}>
                <td><strong>{t.text}</strong></td>
                <td className="wrap">{t.note ?? ''}</td>
                <td>{new Date(t.addedAt).toLocaleDateString('en-GB', { day: '2-digit', month: 'short', year: 'numeric' })}</td>
                {admin && <td>{confirming === t.id
                  ? <div className="actions"><button className="danger" onClick={() => remove(t)}>Remove</button><button onClick={() => setConfirming(null)}>Keep</button></div>
                  : <button onClick={() => setConfirming(t.id)} aria-label={`Remove ${t.text}`}>Remove…</button>}</td>}
              </tr>
            ))}
          </tbody>
        </table>
      </div>
    </div>
  );
}
