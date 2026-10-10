import { useState } from 'react';
import { api } from '../api';

export default function Import() {
  const [file, setFile] = useState<File | null>(null);
  const [name, setName] = useState('');
  const [note, setNote] = useState('');
  const [rights, setRights] = useState(false);
  const [busy, setBusy] = useState(false);
  const [spoken, setSpoken] = useState('en');
  const [speakers, setSpeakers] = useState('');
  const [error, setError] = useState('');

  const submit = async () => {
    if (!file) return;
    setBusy(true); setError('');
    try {
      const f = new FormData();
      f.append('file', file); f.append('name', name); f.append('sourceNote', note); f.append('rightsConfirmed', String(rights)); f.append('language', spoken === 'fr' ? 'fr' : 'en'); f.append('spokenLanguages', spoken); if (speakers) f.append('numSpeakers', speakers);
      f.append('lengthMs', '0');
      const { id } = await api.form('/items/imports', f);
      location.hash = `#/items/${id}`;
    } catch (e: any) { setError(e.message); } finally { setBusy(false); }
  };

  return (
    <div className="card">
      <h2>Import a recording</h2>
      <p className="note">Audio and video files only. The original is kept untouched, scanned for malware and stored read-only.</p>
      <div className="field"><label>File<br /><input type="file" accept="audio/*,video/*,.mp3,.wav,.m4a,.mp4,.webm,.ogg,.opus,.mov,.aac,.flac,.mkv,.wma" onChange={e => setFile(e.target.files?.[0] ?? null)} /></label></div>
      <div className="field"><label>Name (optional)<br /><input value={name} onChange={e => setName(e.target.value)} /></label></div>
      <div className="field"><label>Language spoken<br />
        <select value={spoken} onChange={e => setSpoken(e.target.value)}>
          <option value="en">English</option><option value="fr">French</option><option value="en,fr">English and French (mixed)</option><option value="auto">Not sure: detect automatically</option></select></label>
        <div className="note">One language is transcribed faster and more accurately. Choose mixed only if both are spoken in the same meeting. If you are not sure what is spoken, let it detect automatically, which is a little less accurate and slower.</div></div>
      <div className="field"><label>Number of speakers (optional)<br />
          <select value={speakers} onChange={e => setSpeakers(e.target.value)}>
            <option value="">Not sure</option>{[2, 3, 4, 5, 6, 7, 8, 9, 10].map(n => <option key={n} value={n}>{n}</option>)}</select></label>
          <div className="note">If you know how many people will speak, say so. It helps the app tell them apart.</div></div>
      <div className="field"><label>Where did it come from? (optional)<br /><input value={note} onChange={e => setNote(e.target.value)} /></label></div>
      <label className="row"><input type="checkbox" checked={rights} onChange={e => setRights(e.target.checked)} />
        <span>I confirm I have the right to process this recording and that everyone on it was told it was being recorded.</span></label>
      {error && <div className="error">{error}</div>}
      <button className="primary" disabled={!file || !rights || busy} onClick={submit}>{busy ? 'Uploading and scanning…' : 'Import'}</button>
    </div>
  );
}
