import { useEffect, useState } from 'react';
import { api, type SavedText, type UserView } from '../api';

const MAX_ENTRIES = 500;
const MAX_TEXT = 20000;

/** Saved text: phrases, sentences or paragraphs dropped into a dictation by saying "insert" and the code. */
export default function SavedTextPage() {
  const [entries, setEntries] = useState<SavedText[] | null>(null);
  const [me, setMe] = useState<UserView | null>(null);
  const [search, setSearch] = useState('');
  const [editing, setEditing] = useState<string | null>(null); // 'new' or an entry's id
  const [code, setCode] = useState('');
  const [text, setText] = useState('');
  const [message, setMessage] = useState('');
  const [error, setError] = useState('');
  const [confirming, setConfirming] = useState<string | null>(null);

  const load = () => api.savedText().then(setEntries).catch(e => setError(e.message));
  useEffect(() => { load(); api.me().then(setMe); }, []);
  const admin = me?.isAdmin === true;

  const run = async (fn: () => Promise<unknown>) => { setError(''); setMessage(''); try { await fn(); await load(); } catch (e: any) { setError(e.message); } };
  const open = (e: SavedText | null) => { setEditing(e?.id ?? 'new'); setCode(e?.code ?? ''); setText(e?.text ?? ''); setError(''); setMessage(''); };
  const close = () => { setEditing(null); setCode(''); setText(''); };
  const save = () => run(async () => {
    const saved = editing === 'new' ? await api.addSavedText(code, text) : await api.changeSavedText(editing!, code, text);
    setMessage(editing === 'new' ? `Added "${saved.code}".` : `Saved "${saved.code}". Dictations from now on use the new wording.`);
    close();
  });
  const remove = (e: SavedText) => run(async () => { await api.removeSavedText(e.id); setConfirming(null); setMessage(`Removed "${e.code}".`); });

  const shown = (entries ?? []).filter(e => !search || `${e.code} ${e.text}`.toLowerCase().includes(search.toLowerCase()));
  const date = (d: string) => new Date(d).toLocaleDateString('en-GB', { day: '2-digit', month: 'short', year: 'numeric' });

  const form = (
    <div className="field" style={{ margin: '12px 0' }}>
      <label className="field">Code: what you say after "insert"
        <input value={code} maxLength={60} onChange={e => setCode(e.target.value)} placeholder="e.g. legal 1.1 or disclaimer 2.0" /></label>
      <label className="field">Text to put in (press Enter to start a new paragraph)
        <textarea rows={8} value={text} maxLength={MAX_TEXT} onChange={e => setText(e.target.value)} /></label>
      <div className="actions">
        <button className="primary" disabled={!code.trim() || !text.trim()} onClick={save}>{editing === 'new' ? 'Add' : 'Save changes'}</button>
        <button onClick={close}>Cancel</button>
      </div>
    </div>
  );

  return (
    <div className="card">
      <h2>Saved text</h2>
      <p className="note">Phrases, sentences or whole paragraphs you use often. While dictating, say "insert" and the code, for example "insert legal 1.1",
        and the text is put in its place. Numbers can be said as "one point one" or "one comma one". Changes apply to dictations transcribed from now on,
        not to ones already done.</p>
      {error && <div className="error">{error}</div>}
      {message && <div className="ok" role="status">{message}</div>}
      {admin && editing === null && <div className="actions" style={{ margin: '12px 0' }}><button className="primary" onClick={() => open(null)}>Add saved text</button></div>}
      {admin && editing === 'new' && form}
      {me && !admin && <p className="note">Only administrators can change saved text. Ask one to add anything that is missing.</p>}

      <div className="actions" style={{ margin: '12px 0', alignItems: 'center' }}>
        <input placeholder="Search saved text" value={search} onChange={e => setSearch(e.target.value)} />
        <span className="note">{entries === null ? '' : `${entries.length} of ${MAX_ENTRIES}${search ? ` · ${shown.length} shown` : ''}`}</span>
      </div>
      <div className="scroll">
        <table>
          <thead><tr><th>Say "insert" then</th><th>Text</th><th>Last changed</th>{admin && <th>Actions</th>}</tr></thead>
          <tbody>
            {entries === null && <tr><td colSpan={4}>Loading…</td></tr>}
            {entries !== null && entries.length === 0 && <tr><td colSpan={4} className="note">There is no saved text yet.{admin ? ' Add the first one above.' : ''}</td></tr>}
            {entries !== null && entries.length > 0 && shown.length === 0 && <tr><td colSpan={4} className="note">Nothing matches your search.</td></tr>}
            {shown.map(e => editing === e.id
              ? <tr key={e.id}><td colSpan={4}>{form}</td></tr>
              : <tr key={e.id}>
                <td><strong>{e.code}</strong></td>
                <td className="wrap saved-text">{e.text}</td>
                <td>{date(e.changedAt ?? e.addedAt)}</td>
                {admin && <td>{confirming === e.id
                  ? <div className="actions"><button className="danger" onClick={() => remove(e)}>Remove</button><button onClick={() => setConfirming(null)}>Keep</button></div>
                  : <div className="actions"><button onClick={() => open(e)} aria-label={`Change ${e.code}`}>Change</button>
                    <button onClick={() => setConfirming(e.id)} aria-label={`Remove ${e.code}`}>Remove…</button></div>}</td>}
              </tr>)}
          </tbody>
        </table>
      </div>
    </div>
  );
}
