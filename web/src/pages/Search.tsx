import { useEffect, useState, type ReactNode } from 'react';
import { api, fmtDate, fmtLen } from '../api';

type Hit = { where: string; speaker: string | null; startMs: number | null; segmentId: string | null; text: string; marks: [number, number][] };
type Result = { id: string; name: string; mode: string; status: string; author: string | null; createdOn: string; clientReference: string | null; hits: Hit[]; moreHits: number };
type Answer = { results: Result[]; limited: boolean; problem: string | null };

const MODES: [string, string][] = [['', 'Everything'], ['meeting', 'Meetings'], ['dictate', 'Dictations'], ['speech', 'Speech']];
const MODE_LABEL: Record<string, string> = { Meeting: 'Meeting', Dictate: 'Dictation', Speech: 'Speech' };

/** The search words lit up in a stretch of text. */
function Marked({ text, marks }: { text: string; marks: [number, number][] }) {
  const out: ReactNode[] = [];
  let at = 0;
  marks.forEach(([s, n], k) => {
    if (s < at) return;
    out.push(text.slice(at, s));
    out.push(<mark key={k}>{text.slice(s, s + n)}</mark>);
    at = s + n;
  });
  out.push(text.slice(at));
  return <>{out}</>;
}

export const hitLink = (r: Result, h: Hit) =>
  `#/items/${r.id}` + (h.segmentId ? `?seg=${h.segmentId}${h.startMs ? `&at=${h.startMs}` : ''}` : '');

/** Search: finds recordings by what was said in them (transcripts, summaries, minutes, action points) and by name. */
export default function SearchPage({ query }: { query: URLSearchParams }) {
  const [q, setQ] = useState(query.get('q') ?? '');
  const [mode, setMode] = useState(query.get('mode') ?? '');
  const [answer, setAnswer] = useState<Answer | null>(null);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState('');

  const asked = query.get('q') ?? '';
  const askedMode = query.get('mode') ?? '';
  useEffect(() => {
    if (!asked.trim()) { setAnswer(null); return; }
    setBusy(true); setError('');
    api.get<Answer>(`/search?q=${encodeURIComponent(asked)}${askedMode ? `&mode=${askedMode}` : ''}`)
      .then(setAnswer).catch(e => setError(e.message)).finally(() => setBusy(false));
  }, [asked, askedMode]);

  const go = (text: string, m: string) => { location.hash = `#/search?q=${encodeURIComponent(text.trim())}${m ? `&mode=${m}` : ''}`; };

  return (
    <div className="card">
      <h2>Search</h2>
      <form className="search-form" role="search" onSubmit={e => { e.preventDefault(); go(q, mode); }}>
        <input type="search" aria-label="Search words" placeholder='Words said in a recording, e.g. budget forecast or "next steps"' value={q}
          onChange={e => setQ(e.target.value)} autoFocus maxLength={200} />
        <select aria-label="Which recordings" value={mode} onChange={e => { setMode(e.target.value); if (q.trim()) go(q, e.target.value); }}>
          {MODES.map(([v, l]) => <option key={v} value={v}>{l}</option>)}
        </select>
        <button className="primary" disabled={!q.trim()}>Search</button>
      </form>
      <p className="note">Searches the text of every recording you can open, as it is now (with corrections), plus summaries, minutes, action points and
        names. Every word must appear somewhere in the recording; put words in "quotes" to find them together. Capitals and accents do not matter.</p>
      {error && <div className="error">{error}</div>}
      {answer?.problem && <p className="note">{answer.problem}</p>}
      {busy && <p>Searching…</p>}
      {!busy && answer && !answer.problem && answer.results.length === 0 && <p>Nothing found for <strong>{asked}</strong>.</p>}
      {!busy && answer && answer.results.length > 0 && <p className="note">{answer.results.length}{answer.limited ? '+' : ''} recording{answer.results.length === 1 ? '' : 's'} found
        {answer.limited ? ': showing the newest. Add more words to narrow it down.' : '.'}</p>}
      {!busy && answer?.results.map(r => (
        <section key={r.id} className="result" aria-label={r.name}>
          <h3><a href={r.hits[0] ? hitLink(r, r.hits[0]) : `#/items/${r.id}`}>{r.name}</a></h3>
          <div className="note">{MODE_LABEL[r.mode] ?? r.mode} · <span className="pill">{r.status}</span> · {r.author ?? ''} · {fmtDate(r.createdOn)}{r.clientReference ? ` · ${r.clientReference}` : ''}</div>
          <ul className="hits">
            {r.hits.map((h, k) => (
              <li key={k}>
                <a href={hitLink(r, h)} className="hit-where">{h.where === 'Transcript'
                  ? <>{h.startMs !== null ? fmtLen(h.startMs) : ''}{h.speaker ? ` ${h.speaker}` : ''}</>
                  : h.where}</a>{' '}
                <span className="hit-text"><Marked text={h.text} marks={h.marks} /></span>
              </li>))}
          </ul>
          {r.moreHits > 0 && <div className="note">and {r.moreHits} more place{r.moreHits === 1 ? '' : 's'} in this recording</div>}
        </section>))}
    </div>
  );
}
