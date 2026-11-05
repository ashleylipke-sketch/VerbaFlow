import { useEffect, useState } from 'react';
import { api, saveFile, type MinutesTemplate, type MinutesTemplates, type TemplateInput } from '../api';

const partNames: Record<string, string> = {
  summary: 'Summary', actions: 'Action points', minutes: 'Minutes', tone: 'Tone of the meeting', transcript: 'Transcript',
};

const inputOf = (t: MinutesTemplate): TemplateInput => ({
  name: t.name, isDefault: t.isDefault, title: t.title, font: t.font, colour: t.colour, logoPlace: t.logoPlace, onLetterhead: t.onLetterhead,
  sections: t.sections.map(s => ({ ...s })), transcriptTimes: t.transcriptTimes, footerText: t.footerText,
});

/** Minutes templates: how exported meeting minutes look. Everyone sees them; only admins make and change them. */
export default function MinutesTemplatesPage() {
  const [data, setData] = useState<MinutesTemplates | null>(null);
  const [editing, setEditing] = useState<string | null>(null); // a template id, 'new', or null
  const [form, setForm] = useState<TemplateInput | null>(null);
  const [logoUrl, setLogoUrl] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const [message, setMessage] = useState('');
  const [error, setError] = useState('');
  const [confirmRemove, setConfirmRemove] = useState(false);

  const load = () => api.minutesTemplates().then(setData).catch(e => setError(e.message));
  useEffect(() => { load(); }, []);

  const saved = data?.templates.find(t => t.id === editing) ?? null;

  // Shows the logo of the template being changed.
  useEffect(() => {
    let url: string | null = null;
    setLogoUrl(null);
    if (saved?.hasLogo) api.templateFile(saved.id, 'logo').then(b => { url = URL.createObjectURL(b); setLogoUrl(url); }).catch(() => {});
    return () => { if (url) URL.revokeObjectURL(url); };
  }, [saved?.id, saved?.logoVersion]);

  const run = async (fn: () => Promise<unknown>, done = '') => {
    setBusy(true); setError(''); setMessage('');
    try { await fn(); setMessage(done); } catch (e: any) { setError(e.message); } finally { setBusy(false); }
  };

  const open = (t: MinutesTemplate | null) => {
    setMessage(''); setError(''); setConfirmRemove(false);
    if (t) { setEditing(t.id); setForm(inputOf(t)); }
    else if (data) { setEditing('new'); setForm({ ...inputOf(data.standard), name: '', isDefault: data.templates.length === 0 }); }
  };
  const close = () => { setEditing(null); setForm(null); setConfirmRemove(false); };

  const save = () => form && run(async () => {
    const t = editing === 'new' ? await api.addTemplate(form) : await api.changeTemplate(editing!, form);
    await load();
    setEditing(t.id); setForm(inputOf(t));
  }, editing === 'new' ? 'Template made. You can now add a logo.' : 'Saved. Minutes exported from now on use it.');

  const sample = (id: string, format: 'pdf' | 'docx', name: string) => {
    if (format === 'pdf') {
      const tab = window.open('', '_blank');
      run(async () => {
        try {
          const url = URL.createObjectURL(await api.templateFile(id, 'sample?format=pdf'));
          if (tab) tab.location.href = url; else window.open(url, '_blank');
          setTimeout(() => URL.revokeObjectURL(url), 60_000);
        } catch (e) { tab?.close(); throw e; }
      });
    } else run(async () => saveFile(new File([await api.templateFile(id, 'sample?format=docx&download=true')], `${name} sample.docx`)));
  };

  if (!data) return <div className="card"><h2>Minutes templates</h2>{error ? <div className="error">{error}</div> : <p className="note">Loading…</p>}</div>;
  const admin = data.canChange;
  const standardId = '00000000-0000-0000-0000-000000000000';
  const set = (p: Partial<TemplateInput>) => setForm(f => f && { ...f, ...p });
  const move = (i: number, by: number) => setForm(f => {
    if (!f) return f;
    const s = [...f.sections]; const j = i + by;
    if (j < 0 || j >= s.length) return f;
    [s[i], s[j]] = [s[j], s[i]];
    return { ...f, sections: s };
  });
  const section = (i: number, p: Partial<{ heading: string; on: boolean }>) =>
    setForm(f => f && { ...f, sections: f.sections.map((x, k) => k === i ? { ...x, ...p } : x) });

  const letterheadNote = data.wordLetterhead && data.pdfLetterhead ? 'Word and PDF letterheads are set up.'
    : data.wordLetterhead ? 'Only a Word letterhead is set up, so PDF minutes use a plain page.'
    : data.pdfLetterhead ? 'Only a PDF letterhead is set up, so Word minutes use a plain page.'
    : 'No letterhead is set up yet, so minutes use a plain page. Add one on the Letterhead page.';

  return (
    <div className="card">
      <h2>Minutes templates</h2>
      <p className="note">How exported meeting minutes look: the title, a logo, the font and colour, which parts go in and in what order, and the footer.
        The default template is used unless someone picks another one when exporting. The title, Draft or Approved marker and the details table are always included.</p>
      {!admin && <p className="note">Only administrators can make or change templates.</p>}
      {error && <div className="error" role="alert">{error}</div>}
      {message && <div className="ok" role="status">{message}</div>}

      {!form && <>
        <table className="list">
          <thead><tr><th>Template</th><th>Font</th><th>Parts, in order</th><th></th></tr></thead>
          <tbody>
            {data.templates.map(t => <tr key={t.id}>
              <td data-label="Template"><span className="swatch" style={{ background: t.colour }} /> <strong>{t.name}</strong> {t.isDefault && <span className="tag">Default</span>}
                {t.onLetterhead && <span className="tag">On letterhead</span>}</td>
              <td data-label="Font">{t.font}</td>
              <td data-label="Parts">{t.sections.filter(s => s.on).map(s => s.heading).join(', ')}</td>
              <td className="actions">
                <button disabled={busy} onClick={() => sample(t.id, 'pdf', t.name)}>Preview</button>
                {admin && <button disabled={busy} onClick={() => open(t)}>Change</button>}
              </td>
            </tr>)}
            <tr>
              <td data-label="Template"><strong>{data.standard.name}</strong> {!data.templates.some(t => t.isDefault) && <span className="tag">Default</span>}
                <div className="note">VerbaFlow's own layout, used when there is no default template.</div></td>
              <td data-label="Font">{data.standard.font}</td>
              <td data-label="Parts">{data.standard.sections.map(s => s.heading).join(', ')}</td>
              <td className="actions"><button disabled={busy} onClick={() => sample(standardId, 'pdf', data.standard.name)}>Preview</button></td>
            </tr>
          </tbody>
        </table>
        {admin && <div className="actions"><button className="primary" disabled={busy || data.templates.length >= 20} onClick={() => open(null)}>New template</button></div>}
      </>}

      {form && <div className="template-form">
        <h3>{editing === 'new' ? 'New template' : `Change "${saved?.name}"`}</h3>
        <label className="field">Name<br /><input type="text" maxLength={60} value={form.name} onChange={e => set({ name: e.target.value })} placeholder="For example: Board minutes" /></label>
        <label className="field">Title line<br /><input type="text" maxLength={120} value={form.title} onChange={e => set({ title: e.target.value })} style={{ width: '100%' }} />
          <span className="note">{'{name}'} is the meeting's name and {'{date}'} the day it was recorded. For example: Minutes of {'{name}'}, {'{date}'}</span></label>
        <div className="pair" style={{ flexWrap: 'wrap' }}>
          <label className="field">Font<br /><select value={form.font} onChange={e => set({ font: e.target.value })}>
            {data.fonts.map(f => <option key={f} value={f}>{f}</option>)}</select></label>
          <label className="field">Colour of the title and headings<br />
            <span className="row"><input type="color" aria-label="Colour" value={form.colour} onChange={e => set({ colour: e.target.value })} />
              <input type="text" aria-label="Colour code" maxLength={7} value={form.colour} onChange={e => set({ colour: e.target.value })} style={{ width: 90 }} /></span></label>
        </div>

        <h4>Logo</h4>
        {editing === 'new' ? <p className="note">Save the template first, then add a logo.</p> : <>
          {logoUrl ? <img src={logoUrl} alt="Template logo" className="template-logo" /> : <p className="note">No logo.</p>}
          <div className="actions">
            <label className="btn">{saved?.hasLogo ? 'Replace logo…' : 'Add a logo…'}
              <input type="file" hidden accept=".png,.jpg,.jpeg" disabled={busy} onChange={e => {
                const f = e.target.files?.[0]; e.target.value = '';
                if (f && saved) run(async () => { await api.uploadTemplateLogo(saved.id, f); await load(); }, 'Logo saved.');
              }} /></label>
            {saved?.hasLogo && <button disabled={busy} onClick={() => run(async () => { await api.removeTemplateLogo(saved.id); await load(); }, 'Logo removed.')}>Remove logo</button>}
          </div>
          <p className="note">A PNG or JPEG picture of up to 1 MB, printed at the top of the first page.</p>
        </>}
        <div className="row">Place it at the
          <label className="check"><input type="radio" name="logo-place" checked={form.logoPlace === 'left'} onChange={() => set({ logoPlace: 'left' })} /> left</label>
          <label className="check"><input type="radio" name="logo-place" checked={form.logoPlace === 'right'} onChange={() => set({ logoPlace: 'right' })} /> right</label></div>
        <label className="check"><input type="checkbox" checked={form.onLetterhead} onChange={e => set({ onLetterhead: e.target.checked })} /> Lay the minutes out on the organisation's letterhead</label>
        <p className="note">{letterheadNote}</p>

        <h4>Parts</h4>
        <p className="note">Tick the parts included by default (people can still change the ticks when exporting), give each its heading, and use the arrows to change the order.</p>
        <ol className="template-sections">
          {form.sections.map((s, i) => <li key={s.part}>
            <input type="checkbox" aria-label={`Include ${partNames[s.part]}`} checked={s.on} onChange={e => section(i, { on: e.target.checked })} />
            <input type="text" aria-label={`Heading for ${partNames[s.part]}`} maxLength={60} value={s.heading} onChange={e => section(i, { heading: e.target.value })} />
            <span className="note">{partNames[s.part]}</span>
            <button aria-label={`Move ${partNames[s.part]} up`} disabled={i === 0} onClick={() => move(i, -1)}>↑</button>
            <button aria-label={`Move ${partNames[s.part]} down`} disabled={i === form.sections.length - 1} onClick={() => move(i, 1)}>↓</button>
          </li>)}
        </ol>
        <label className="check"><input type="checkbox" checked={form.transcriptTimes} onChange={e => set({ transcriptTimes: e.target.checked })} /> Show the time beside each line of the transcript</label>

        <label className="field">Footer text<br /><input type="text" maxLength={80} value={form.footerText} onChange={e => set({ footerText: e.target.value })} placeholder="For example: Confidential" />
          <span className="note">Printed first in the footer, before the meeting's name, the marker and the reference, which are always there.</span></label>
        <label className="check"><input type="checkbox" checked={form.isDefault} onChange={e => set({ isDefault: e.target.checked })} /> Use this template by default</label>

        <div className="actions">
          <button className="primary" disabled={busy || !form.name.trim()} onClick={save}>{editing === 'new' ? 'Make the template' : 'Save'}</button>
          {saved && <button disabled={busy} onClick={() => sample(saved.id, 'pdf', saved.name)}>Preview (PDF)</button>}
          {saved && <button disabled={busy} onClick={() => sample(saved.id, 'docx', saved.name)}>Download a sample (Word)</button>}
          <button disabled={busy} onClick={() => { close(); setMessage(''); }}>Back to the list</button>
          {saved && !confirmRemove && <button className="danger" disabled={busy} onClick={() => setConfirmRemove(true)}>Remove…</button>}
          {saved && confirmRemove && <span className="row">Remove this template? <button className="danger" disabled={busy}
            onClick={() => run(async () => { await api.removeTemplate(saved.id); close(); await load(); }, 'Template removed.')}>Yes, remove</button>
            <button onClick={() => setConfirmRemove(false)}>No</button></span>}
        </div>
        {saved && <p className="note">Preview and the sample use the template as last saved, with a made-up meeting. Save first to see a change.</p>}
      </div>}
    </div>
  );
}
