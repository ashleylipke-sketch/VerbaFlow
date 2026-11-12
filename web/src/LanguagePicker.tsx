import { useEffect, useState } from 'react';
import { api, type Lang } from './api';
import { parseSpoken } from './languages';

/**
 * Language spoken: pick one language, or add up to a few more when several are spoken in the same recording.
 * live: only languages that have live text (Speech mode). A language marked onlyLanguage (the South African languages
 * sent to Google) cannot be combined with another.
 */
export default function LanguagePicker({ value, onChange, allowAuto = true, live = false, note }:
  { value: string; onChange: (v: string) => void; allowAuto?: boolean; live?: boolean; note?: string }) {
  const [all, setAll] = useState<Lang[]>([]);
  const [max, setMax] = useState(3);
  useEffect(() => { api.languages().then(r => { setAll(r.languages); setMax(r.max); }).catch(() => { /* the list stays empty */ }); }, []);
  const langs = all.filter(l => !live || l.live !== false);
  const chosen = parseSpoken(value);
  const set = (next: string[]) => onChange(next.length ? next.join(',') : 'en-GB');
  const only = (code: string) => all.some(l => l.code === code && l.onlyLanguage);
  const alone = chosen.some(only);
  const usable = langs.filter(l => l.supported);
  const options = (self: string, i: number) => <>
    {allowAuto && self === chosen[0] && <option value="auto">Not sure: detect automatically</option>}
    {usable.filter(l => i === 0 || !l.onlyLanguage || l.code === self).map(l =>
      <option key={l.code} value={l.code} disabled={l.code !== self && chosen.includes(l.code)}>{l.name}{l.onlyLanguage ? ' (only language)' : ''}</option>)}
    {langs.some(l => !l.supported) && <optgroup label="Not available yet">
      {langs.filter(l => !l.supported).map(l => <option key={l.code} value={l.code} disabled>{l.name}: {l.why ?? 'not yet supported'}</option>)}</optgroup>}
  </>;
  return <div className="field"><label>Language spoken</label>
    {chosen.map((c, i) => <div key={i} style={{ display: 'flex', gap: 8, marginBottom: 6 }}>
      <select style={{ minWidth: 0, flex: 1 }} aria-label={i === 0 ? 'Language spoken' : `Another language ${i}`} value={c}
        onChange={e => {
          const v = e.target.value;
          if (v === 'auto' || only(v)) return set([v]); // detect automatically, or a language that must be alone
          const n = [...chosen]; n[i] = v; set(n);
        }}>
        {options(c, i)}</select>
      {i > 0 && <button type="button" className="link" onClick={() => set(chosen.filter((_, k) => k !== i))}>Remove</button>}
    </div>)}
    {chosen[0] !== 'auto' && !alone && chosen.length < max &&
      <button type="button" className="link" onClick={() => set([...chosen, usable.find(l => !l.onlyLanguage && !chosen.includes(l.code))?.code ?? 'en-GB'])}>
        + Another language is also spoken</button>}
    {alone && <div className="note">This language is converted to text by Google's speech service. It cannot be mixed with another language in one recording, there is no live text for it, and in a meeting the speakers are worked out by VerbaFlow's own voice model, which is less reliable. Accuracy is lower than for English: in tests on public recordings Google got between about 1 in 10 and 1 in 3 words wrong.</div>}
    {!alone && <div className="note">{note ?? <>One language is transcribed fastest and most accurately. If you add a second or third, the recording is cut into short pieces and the language is worked out piece by piece, so people can switch between languages. That takes longer. If you are not sure what is spoken, choose detect automatically, which is a little less accurate and slower. Sesotho and isiNdebele cannot be transcribed yet.</>}</div>}</div>;
}
