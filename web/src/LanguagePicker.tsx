import { useEffect, useState } from 'react';
import { api, type Lang } from './api';
import { parseSpoken } from './languages';

/** Language spoken: pick one language, or add up to a few more when several are spoken in the same recording. */
export default function LanguagePicker({ value, onChange }: { value: string; onChange: (v: string) => void }) {
  const [langs, setLangs] = useState<Lang[]>([]);
  const [max, setMax] = useState(3);
  useEffect(() => { api.languages().then(r => { setLangs(r.languages); setMax(r.max); }).catch(() => { /* the list stays empty */ }); }, []);
  const chosen = parseSpoken(value);
  const set = (next: string[]) => onChange(next.length ? next.join(',') : 'en-GB');
  const options = (self: string) => <>
    {self === chosen[0] && <option value="auto">Not sure: detect automatically</option>}
    {langs.filter(l => l.supported).map(l => <option key={l.code} value={l.code} disabled={l.code !== self && chosen.includes(l.code)}>{l.name}</option>)}
    {langs.some(l => !l.supported) && <optgroup label="Not available yet">
      {langs.filter(l => !l.supported).map(l => <option key={l.code} value={l.code} disabled>{l.name}: not yet supported</option>)}</optgroup>}
  </>;
  return <div className="field"><label>Language spoken</label>
    {chosen.map((c, i) => <div key={i} style={{ display: 'flex', gap: 8, marginBottom: 6 }}>
      <select aria-label={i === 0 ? 'Language spoken' : `Another language ${i}`} value={c}
        onChange={e => { const n = [...chosen]; n[i] = e.target.value; set(e.target.value === 'auto' ? ['auto'] : n); }}>
        {options(c)}</select>
      {i > 0 && <button type="button" className="link" onClick={() => set(chosen.filter((_, k) => k !== i))}>Remove</button>}
    </div>)}
    {chosen[0] !== 'auto' && chosen.length < max &&
      <button type="button" className="link" onClick={() => set([...chosen, langs.find(l => l.supported && !chosen.includes(l.code))?.code ?? 'en-GB'])}>
        + Another language is also spoken</button>}
    <div className="note">One language is transcribed fastest and most accurately. If you add a second or third, the recording is transcribed once in each language and, for each stretch of speech, the more confident version is kept. That takes longer and uses the speech service once per language. If you are not sure what is spoken, choose detect automatically, which is a little less accurate and slower. Some languages, such as isiXhosa and Sesotho, cannot be transcribed yet.</div></div>;
}
