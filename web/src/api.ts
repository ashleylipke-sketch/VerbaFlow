export type Row = {
  id: string; name: string; priority: string; status: string; statusCode: string; lengthMs: number;
  clientReference: string | null; description: string | null; assignedTo: string | null; author: string | null; createdOn: string;
  dueDate: string | null; overdue: boolean; tags: string[]; actions: string[]; versionNo: number;
};
export type Detail = {
  row: Row; owner: string; ownerRole: string; processingState: string | null; failureReason: string | null;
  originalFileName: string | null; externalSourceNote: string | null; originalSha256: string | null;
  markers: { type: string; offsetMs: number; note: string | null; by: string; at: string }[];
  canEditTranscript: boolean; canEditDetails: boolean; canDownloadAudio: boolean; role: string; outputLanguage: string;
  approvedAt: string | null; approvedBy: string | null; chainId: string;
  chain: { id: string; versionNo: number; status: string; isCurrent: boolean }[]; provenance: string; spokenLanguages: string;
  deletedAt: string | null; deletedBy: string | null; deleteReason: string | null; canExport: boolean;
  mode: 'Dictate' | 'Meeting'; documentType: string | null; clientReference: string | null;
};
export type Segment = { id: string; speaker: string; speakerId: string; language: string; startMs: number; endMs: number; text: string; lowConfidence: boolean; words: { text: string; startMs: number; endMs: number }[] | null; originalText: string | null; newParagraph: boolean | null; list?: 'bullet' | 'number' | null };
export type Transcript = {
  versionNo: number; engine: string; segments: Segment[]; speakers: { id: string; label: string; name: string }[];
  versions: { no: number; kind: string; capacity: string; by: string | null; at: string; note: string | null }[];
};
export type Outputs = { engine: string; language: string; summary: string; actionPoints: string[]; minutes: string; toneOfMeeting: string };
export type LetterheadFile = { sha256: string; fileName: string; sizeBytes: number; uploadedBy: string; uploadedAt: string };
export type Letterhead = { word: LetterheadFile | null; pdf: LetterheadFile | null; pdfTopFirstMm: number; pdfTopNextMm: number; pdfBottomMm: number; pdfSideMm: number };
export type UserView = { id: string; name: string; email: string; isAdmin: boolean; isSupport: boolean };
export type SupportError = { id: string; reference: string; area: string; kind: string; itemId: string | null; detail: string; at: string };
export type AuditEvent = { seq: number; at: string; actorId: string | null; capacity: string; type: string; details: string; hash: string };

/** One readable line for the History list: what happened, then the recorded facts (empty ones left out). */
export function describeAudit(a: AuditEvent): string {
  let facts = '';
  try {
    const o = JSON.parse(a.details) as Record<string, unknown>;
    facts = Object.entries(o).filter(([, v]) => v !== null && v !== '' && v !== undefined)
      .map(([k, v]) => `${k}: ${v}`).join(', ');
  } catch { facts = a.details ?? ''; }
  if (facts.length > 900) facts = facts.slice(0, 900) + '…';
  return facts ? `${a.type} (${facts})` : a.type;
}
export type DeleteRequest = { id: string; itemId: string; itemName: string; reason: string; requestedBy: string; approvals: number; approvedBy: string[]; open: boolean; outcome: string | null };
export type Lang = { code: string; name: string; supported: boolean };
export type SavedText = { id: string; code: string; text: string; addedBy: string; addedAt: string; changedBy: string | null; changedAt: string | null };
export type Term = { id: string; text: string; note: string | null; addedBy: string; addedAt: string };
export type Reopen = { id: string; itemId: string; itemName: string; reason: string; requestedBy: string; approvals: number; approvedBy: string[]; open: boolean; outcome: string | null; newItemId: string | null };

let devUser = localStorage.getItem('devUser') ?? '';
/** Speak's modes that are built so far. */
export type Mode = 'dictate' | 'meeting';

export const getUser = () => devUser;
export const setUser = (u: string) => { devUser = u; localStorage.setItem('devUser', u); };

async function call<T>(method: string, path: string, body?: unknown): Promise<T> {
  const init: RequestInit = { method, headers: { 'X-Dev-User': devUser } };
  if (body instanceof FormData) init.body = body;
  else if (body !== undefined) { (init.headers as Record<string, string>)['Content-Type'] = 'application/json'; init.body = JSON.stringify(body); }
  let res: Response;
  try { res = await fetch('/api' + path, init); }
  catch { throw new Error('VerbaFlow could not be reached. Check your connection and try again.'); }
  if (!res.ok) {
    let msg = res.statusText;
    try { msg = (await res.json()).error ?? msg; } catch { /* keep status text */ }
    throw new Error(msg);
  }
  return res.status === 204 ? (undefined as T) : res.json();
}

/** Hands a file to the browser's download. */
export function saveFile(f: File) {
  const a = document.createElement('a');
  a.href = URL.createObjectURL(f);
  a.download = f.name;
  a.click();
  setTimeout(() => URL.revokeObjectURL(a.href), 60_000);
}

export const api = {
  users: () => call<UserView[]>('GET', '/users'),
  languages: () => call<{ max: number; languages: Lang[] }>('GET', '/languages'),
  me: () => call<UserView>('GET', '/me'),
  supportErrors: (reference?: string) => call<SupportError[]>('GET', `/support/errors${reference ? `?reference=${encodeURIComponent(reference)}` : ''}`),
  items: (mode: Mode = 'meeting') => call<Row[]>('GET', `/items?mode=${mode}`),
  item: (id: string) => call<Detail>('GET', `/items/${id}`),
  transcript: (id: string, v?: number) => call<Transcript>('GET', `/items/${id}/transcript${v ? `?version=${v}` : ''}`),
  outputs: (id: string) => call<Outputs | null>('GET', `/items/${id}/outputs`),
  audit: (id: string) => call<AuditEvent[]>('GET', `/items/${id}/audit`),
  verify: () => call<{ ok: boolean; count: number; problem: string | null }>('GET', '/audit/verify'),
  post: (path: string, body?: unknown) => call<any>('POST', path, body ?? {}),
  put: (path: string, body: unknown) => call<void>('PUT', path, body),
  form: (path: string, f: FormData) => call<{ id: string }>('POST', path, f),
  vocabulary: () => call<Term[]>('GET', '/vocabulary'),
  addTerm: (text: string, note: string) => call<Term>('POST', '/vocabulary', { text, note: note || null }),
  importTerms: (lines: string) => call<{ added: number; skipped: string[] }>('POST', '/vocabulary/import', { lines }),
  removeTerm: (id: string) => call<void>('DELETE', `/vocabulary/${id}`),
  savedText: () => call<SavedText[]>('GET', '/saved-text'),
  addSavedText: (code: string, text: string) => call<SavedText>('POST', '/saved-text', { code, text }),
  changeSavedText: (id: string, code: string, text: string) => call<SavedText>('PUT', `/saved-text/${id}`, { code, text }),
  removeSavedText: (id: string) => call<void>('DELETE', `/saved-text/${id}`),
  letterhead: () => call<Letterhead>('GET', '/letterhead'),
  uploadLetterhead: (kind: 'word' | 'pdf', file: File) => { const f = new FormData(); f.append('file', file); return call<Letterhead>('POST', `/letterhead/${kind}`, f); },
  removeLetterhead: (kind: 'word' | 'pdf') => call<Letterhead>('DELETE', `/letterhead/${kind}`),
  letterheadSpace: (s: { topFirstMm: number; topNextMm: number; bottomMm: number; sideMm: number }) => call<Letterhead>('PUT', '/letterhead/space', s),
  /** A file from the letterhead area (the stored letterhead, or a sample letter on it). */
  async letterheadFile(path: string): Promise<Blob> {
    const res = await fetch(`/api/letterhead/${path}`, { headers: { 'X-Dev-User': devUser } });
    if (!res.ok) throw new Error((await res.json().catch(() => ({}))).error ?? res.statusText);
    return res.blob();
  },
  reopenRequests: () => call<Reopen[]>('GET', '/reopen'),
  deleteRequests: () => call<DeleteRequest[]>('GET', '/delete-requests'),
  checkExport: (id: string, file: File) => { const f = new FormData(); f.append('file', file); return call<ExportCheck>('POST', `/items/${id}/export/check`, f); },
  /** Downloads the minutes as Word or PDF and saves them under the name the server gives. */
  /** Fetches an export (the file itself, or an unsent email with it attached) with the name the server gives. */
  async exportFile(id: string, format: 'docx' | 'pdf', parts: string[], kind: 'export' | 'draft' | 'share' = 'export'): Promise<File> {
    const path = kind === 'draft' ? 'export/draft' : 'export';
    const res = await fetch(`/api/items/${id}/${path}?format=${format}&parts=${parts.join(',')}${kind === 'share' ? '&purpose=share' : ''}`, { headers: { 'X-Dev-User': devUser } });
    if (!res.ok) throw new Error((await res.json().catch(() => ({}))).error ?? res.statusText);
    const cd = res.headers.get('Content-Disposition') ?? '';
    const name = /filename\*=UTF-8''([^;]+)/i.exec(cd)?.[1] ?? /filename="?([^";]+)"?/i.exec(cd)?.[1] ?? `meeting.${format}`;
    const blob = await res.blob();
    return new File([blob], decodeURIComponent(name), { type: blob.type });
  },
  /** Downloads the minutes as Word or PDF and saves them under the name the server gives. */
  async exportMinutes(id: string, format: 'docx' | 'pdf', parts: string[], kind: 'export' | 'draft' = 'export'): Promise<void> {
    saveFile(await api.exportFile(id, format, parts, kind));
  },
  /** The PDF as the person would download it, for showing in a browser tab. */
  async previewMinutes(id: string, parts: string[]): Promise<Blob> {
    const res = await fetch(`/api/items/${id}/export?format=pdf&preview=true&parts=${parts.join(',')}`, { headers: { 'X-Dev-User': devUser } });
    if (!res.ok) throw new Error((await res.json().catch(() => ({}))).error ?? res.statusText);
    return res.blob();
  },
  emailMinutes: (id: string, format: 'docx' | 'pdf', to: string, note: string, parts: string[]) =>
    call<{ message: string }>('POST', `/items/${id}/export/email`, { format, to, note: note.trim() || null, parts: parts.join(',') }),
  async audio(id: string, download = false): Promise<Blob> {
    const res = await fetch(`/api/items/${id}/audio${download ? '?download=true' : ''}`, { headers: { 'X-Dev-User': devUser } });
    if (!res.ok) throw new Error((await res.json().catch(() => ({}))).error ?? res.statusText);
    return res.blob();
  },
};

export interface ExportCheck { matches: boolean; message: string; sha256: string }

export const fmtLen = (ms: number) => {
  const s = Math.round(ms / 1000), h = Math.floor(s / 3600), m = Math.floor((s % 3600) / 60), x = s % 60;
  const p = (n: number) => String(n).padStart(2, '0');
  return h ? `${h}:${p(m)}:${p(x)}` : `${m}:${p(x)}`;
};
export const fmtDate = (iso: string) => new Date(iso).toLocaleDateString('en-GB', { day: '2-digit', month: 'short', year: 'numeric' });
