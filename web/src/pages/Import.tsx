import { useState } from 'react';
import { api, type Mode } from '../api';
import LanguagePicker from '../LanguagePicker';
import DocumentTypePicker, { type DocType } from '../DocumentType';
import { summaryLanguage } from '../languages';

export default function Import({ mode }: { mode: Mode }) {
  const dictate = mode === 'dictate';
  const [file, setFile] = useState<File | null>(null);
  const [name, setName] = useState('');
  const [clientRef, setClientRef] = useState('');
  const [docType, setDocType] = useState<DocType | ''>('');
  const [note, setNote] = useState('');
  const [rights, setRights] = useState(false);
  const [busy, setBusy] = useState(false);
  const [spoken, setSpoken] = useState('en-GB');
  const [speakers, setSpeakers] = useState('');
  const [error, setError] = useState('');

  const submit = async () => {
    if (!file) return;
    setBusy(true); setError('');
    try {
      const f = new FormData();
      f.append('file', file); f.append('name', name); f.append('sourceNote', note); f.append('rightsConfirmed', String(rights)); f.append('language', summaryLanguage(spoken)); f.append('spokenLanguages', spoken); if (speakers) f.append('numSpeakers', speakers);
      f.append('lengthMs', '0'); f.append('mode', mode); if (dictate) f.append('documentType', docType); if (clientRef.trim()) f.append('clientReference', clientRef.trim());
      const { id } = await api.form('/items/imports', f);
      location.hash = `#/items/${id}`;
    } catch (e: any) { setError(e.message); } finally { setBusy(false); }
  };

  const dictateNeeds = !dictate ? '' : !docType ? 'Choose letter, Word document or email.'
    : !name.trim() ? (clientRef.trim() ? 'A client reference needs a name as well.' : 'Give the dictation a name.') : '';

  return (
    <div className="card">
      <h2>{dictate ? 'Import a dictation' : 'Import a recording'}</h2>
      <p className="note">Audio and video files only. The original is kept untouched, scanned for malware and stored read-only.</p>
      <div className="field"><label>File<br /><input type="file" accept="audio/*,video/*,.mp3,.wav,.m4a,.mp4,.webm,.ogg,.opus,.mov,.aac,.flac,.mkv,.wma" onChange={e => setFile(e.target.files?.[0] ?? null)} /></label></div>
      {dictate && <DocumentTypePicker value={docType} onChange={setDocType} />}
      <div className="pair"><div className="field"><label>{dictate ? 'Name' : 'Name (optional)'}<br /><input value={name} onChange={e => setName(e.target.value)} /></label></div>
        <div className="field"><label>Client reference (optional)<br /><input value={clientRef} onChange={e => setClientRef(e.target.value)} placeholder="e.g. ACME-042" /></label></div></div>
      <LanguagePicker value={spoken} onChange={setSpoken} />
      {dictate ? <p className="note">For a dictation made on a dictaphone or phone. Spoken commands such as "comma", "full stop" and "new paragraph" are turned into punctuation.</p>
        : <div className="field"><label>Number of speakers (optional)<br />
          <select value={speakers} onChange={e => setSpeakers(e.target.value)}>
            <option value="">Not sure</option>{[2, 3, 4, 5, 6, 7, 8, 9, 10].map(n => <option key={n} value={n}>{n}</option>)}</select></label>
          <div className="note">If you know how many people will speak, say so. It helps the app tell them apart.</div></div>}
      <div className="field"><label>Where did it come from? (optional)<br /><input value={note} onChange={e => setNote(e.target.value)} /></label></div>
      <label className="row"><input type="checkbox" checked={rights} onChange={e => setRights(e.target.checked)} />
        <span>{dictate ? 'I confirm I have the right to process this recording.' : 'I confirm I have the right to process this recording and that everyone on it was told it was being recorded.'}</span></label>
      {error && <div className="error">{error}</div>}
      {dictate && dictateNeeds && <p className="need">{dictateNeeds}</p>}
      <button className="primary" disabled={!file || !rights || busy || !!dictateNeeds} onClick={submit}>{busy ? 'Uploading and scanning…' : 'Import'}</button>
    </div>
  );
}
