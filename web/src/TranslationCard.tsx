import { useEffect, useState } from 'react';
import { api, type Translations } from './api';

const when = (iso: string) => new Date(iso).toLocaleString('en-GB', { day: 'numeric', month: 'short', hour: '2-digit', minute: '2-digit' });

/**
 * Translate: a machine translation of the text (and, for a meeting, its summary and minutes) into another language,
 * opened on its own page beside the original and exported from there or from Export ▾. The recording never changes.
 */
export default function TranslationCard({ id, onChange }: { id: string; onChange?: () => void }) {
  const [l, setL] = useState<Translations | null>(null);
  const [language, setLanguage] = useState('');
  const [busy, setBusy] = useState('');
  const [error, setError] = useState('');
  const load = () => api.get<Translations>(`/items/${id}/translations`).then(setL).catch(e => setError(e.message));
  useEffect(() => { setL(null); load(); }, [id]);

  const translate = async (code: string) => {
    setBusy(code); setError('');
    try { await api.post(`/items/${id}/translations`, { language: code }); await load(); onChange?.(); setLanguage(''); }
    catch (e: any) { setError(e.message); } finally { setBusy(''); }
  };
  const remove = async (code: string, name: string) => {
    if (!confirm(`Remove the ${name} translation? You can make it again later.`)) return;
    setError('');
    try { await api.del(`/items/${id}/translations/${code}`); await load(); onChange?.(); } catch (e: any) { setError(e.message); }
  };
  if (!l) return error ? <div className="card"><h2>Translate</h2><div className="error">{error}</div></div> : null;
  if (!l.canTranslate && l.translations.length === 0) return null;
  const have = new Set(l.translations.map(t => t.language));
  const choices = l.languages.filter(x => !have.has(x.code));

  return (
    <div className="card translation-card">
      <h2>Translate</h2>
      {l.translations.map(t => <div key={t.language} className="translation-row">
        <div><a href={`#/items/${id}/translation/${t.language}`}><strong>{t.name}</strong></a>{' '}
          <span className="note">by {t.madeBy}, {when(t.madeAt)}</span></div>
        {!t.upToDate && <div className="note warn-text">The text has changed since this was translated.</div>}
        {t.untranslated > 0 && <div className="note">{t.untranslated} part{t.untranslated === 1 ? '' : 's'} could not be translated and {t.untranslated === 1 ? 'is' : 'are'} shown as spoken.</div>}
        <div className="actions">
          <button onClick={() => { location.hash = `#/items/${id}/translation/${t.language}`; }}>Open</button>
          {l.canTranslate && <button disabled={!!busy} onClick={() => translate(t.language)}>
            {busy === t.language ? 'Translating…' : t.upToDate ? 'Translate again' : 'Update translation'}</button>}
          {l.canTranslate && <button className="link" disabled={!!busy} onClick={() => remove(t.language, t.name)}>Remove</button>}
        </div>
      </div>)}
      {l.canTranslate && choices.length > 0 && <div className="actions">
        <select aria-label="Translate into" value={language} onChange={e => setLanguage(e.target.value)}>
          <option value="">Translate into…</option>
          {choices.map(x => <option key={x.code} value={x.code}>{x.name}</option>)}
        </select>
        <button className="primary" disabled={!language || !!busy} onClick={() => translate(language)}>
          {busy && busy === language ? 'Translating…' : 'Translate'}</button>
      </div>}
      {l.canTranslate && <p className="note">A machine translation to read or send. The recording itself does not change. Not checked by a person.</p>}
      {!l.canTranslate && l.whyNot && <p className="note">{l.whyNot}</p>}
      {error && <div className="error" role="alert">{error}</div>}
    </div>
  );
}
