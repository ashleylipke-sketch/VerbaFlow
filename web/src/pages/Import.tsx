import { useState } from 'react';
import { api } from '../api';

export default function Import() {
  const [file, setFile] = useState<File | null>(null);
  const [name, setName] = useState('');
  const [note, setNote] = useState('');
  const [rights, setRights] = useState(false);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState('');

  const submit = async () => {
    if (!file) return;
    setBusy(true); setError('');
    try {
      const f = new FormData();
      f.append('file', file); f.append('name', name); f.append('sourceNote', note); f.append('rightsConfirmed', String(rights)); f.append('language', 'en');
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
      <div className="field"><label>Where did it come from? (optional)<br /><input value={note} onChange={e => setNote(e.target.value)} /></label></div>
      <label className="row"><input type="checkbox" checked={rights} onChange={e => setRights(e.target.checked)} />
        <span>I confirm I have the right to process this recording and that everyone on it was told it was being recorded.</span></label>
      {error && <div className="error">{error}</div>}
      <button className="primary" disabled={!file || !rights || busy} onClick={submit}>{busy ? 'Uploading and scanning…' : 'Import'}</button>
    </div>
  );
}
