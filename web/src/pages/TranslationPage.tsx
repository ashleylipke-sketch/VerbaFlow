import { useEffect, useState } from 'react';
import { api, type Detail, type TranslationPage as Page } from '../api';
import { ExportMenu } from '../ExportMenu';

/** Two columns, original and translation; on a phone they sit one above the other. */
function Pair({ original, translated, lang }: { original: string | null; translated: string | null; lang: string }) {
  return <div className="tr-pair">
    <div className="tr-original">{original}</div>
    <div className="tr-translated" lang={lang} dir={lang === 'ar' ? 'rtl' : undefined}>{translated}</div>
  </div>;
}

/** A recording's machine translation beside the original, with Export ▾ for the translated file. */
export default function TranslationPage({ id, lang }: { id: string; lang: string }) {
  const [p, setP] = useState<Page | null>(null);
  const [item, setItem] = useState<Detail | null>(null);
  const [error, setError] = useState('');
  const [busy, setBusy] = useState(false);
  const load = () => {
    api.get<Page>(`/items/${id}/translations/${lang}`).then(setP).catch(e => setError(e.message));
    api.get<Detail>(`/items/${id}`).then(setItem).catch(() => {});
  };
  useEffect(() => { setP(null); setError(''); load(); }, [id, lang]);
  const again = async () => {
    setBusy(true); setError('');
    try { await api.post(`/items/${id}/translations`, { language: lang }); load(); } catch (e: any) { setError(e.message); } finally { setBusy(false); }
  };
  const back = <p className="back"><a href={`#/items/${id}`}>← Back to the recording</a></p>;
  if (!p) return <>{back}{error ? <div className="error">{error}</div> : <p className="note">Loading…</p>}</>;
  const name = p.row.name;
  const meeting = !p.dictation;

  return (
    <>
      {back}
      <div className="card">
        <div className="item-head">
          <h2>{p.itemName} <span className="note">· {name} translation</span></h2>
          {item?.canExport && p.row.upToDate && <ExportMenu id={id} approved={item.row.statusCode === 'Completed'} dictate={p.dictation}
            documentType={item.documentType} onDone={() => {}} language={lang} />}
        </div>
        <p className="note">Machine translation ({p.engine}) made by {p.row.madeBy} on {new Date(p.row.madeAt).toLocaleString('en-GB')}.
          Not checked by a person: the original is the official record.</p>
        {!p.row.upToDate && <div className="banner" role="status">The text has changed since this was translated, so it cannot be exported until it is translated again.
          {p.canTranslate && <div className="actions" style={{ marginTop: 6 }}>
            <button className="primary" disabled={busy} onClick={again}>{busy ? 'Translating…' : 'Translate again'}</button></div>}</div>}
        {p.row.untranslated > 0 && <p className="note">{p.row.untranslated} part{p.row.untranslated === 1 ? '' : 's'} could not be translated and {p.row.untranslated === 1 ? 'is' : 'are'} shown as spoken.</p>}
        {error && <div className="error" role="alert">{error}</div>}
        <div className="tr-pair tr-head"><div>Original</div><div>{name}</div></div>
      </div>
      {meeting && (p.summaryTranslated || p.minutesTranslated || p.actionsTranslated.length > 0) && <div className="card">
        {p.summaryTranslated && <><h2>Summary</h2><Pair original={p.summary} translated={p.summaryTranslated} lang={lang} /></>}
        {p.actionsTranslated.length > 0 && <><h3>Action points</h3>
          {p.actionsTranslated.map((a, i) => <Pair key={i} original={p.actions[i] ?? ''} translated={a} lang={lang} />)}</>}
        {p.minutesTranslated && <><h3>Minutes</h3><Pair original={p.minutes} translated={p.minutesTranslated} lang={lang} /></>}
        {p.toneTranslated && <><h3>Tone</h3><Pair original={p.tone} translated={p.toneTranslated} lang={lang} /></>}
      </div>}
      <div className="card">
        <h2>{meeting ? 'Transcript' : 'Text'}</h2>
        {p.lines.map((l, i) => <div key={i} className="tr-line">
          {meeting && <div className="note">{l.time} · <strong>{l.speaker}</strong></div>}
          <Pair original={(l.marker ? l.marker + ' ' : '') + l.original}
            translated={l.translated ? (l.marker ? l.marker + ' ' : '') + l.translated : null} lang={lang} />
          {!l.translated && <div className="note">Changed since the translation was made.</div>}
        </div>)}
      </div>
    </>
  );
}
