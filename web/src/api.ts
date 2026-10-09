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
  chain: { id: string; versionNo: number; status: string; isCurrent: boolean }[]; provenance: string;
};
export type Segment = { id: string; speaker: string; speakerId: string; language: string; startMs: number; endMs: number; text: string; lowConfidence: boolean; words: { text: string; startMs: number; endMs: number }[] | null; originalText: string | null };
export type Transcript = {
  versionNo: number; engine: string; segments: Segment[]; speakers: { id: string; label: string; name: string }[];
  versions: { no: number; kind: string; capacity: string; by: string | null; at: string; note: string | null }[];
};
export type Outputs = { engine: string; language: string; summary: string; actionPoints: string[]; minutes: string; toneOfMeeting: string };
export type UserView = { id: string; name: string; email: string; isAdmin: boolean; isSupport: boolean };
export type SupportError = { id: string; reference: string; area: string; kind: string; itemId: string | null; detail: string; at: string };
export type AuditEvent = { seq: number; at: string; actor: string; action: string; details: string; hash: string };
export type Term = { id: string; text: string; note: string | null; addedBy: string; addedAt: string };
export type Reopen = { id: string; itemId: string; itemName: string; reason: string; requestedBy: string; approvals: number; approvedBy: string[]; open: boolean; outcome: string | null; newItemId: string | null };

let devUser = localStorage.getItem('devUser') ?? '';
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

export const api = {
  users: () => call<UserView[]>('GET', '/users'),
  me: () => call<UserView>('GET', '/me'),
  supportErrors: (reference?: string) => call<SupportError[]>('GET', `/support/errors${reference ? `?reference=${encodeURIComponent(reference)}` : ''}`),
  items: () => call<Row[]>('GET', '/items'),
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
  reopenRequests: () => call<Reopen[]>('GET', '/reopen'),
  async audio(id: string, download = false): Promise<Blob> {
    const res = await fetch(`/api/items/${id}/audio${download ? '?download=true' : ''}`, { headers: { 'X-Dev-User': devUser } });
    if (!res.ok) throw new Error((await res.json().catch(() => ({}))).error ?? res.statusText);
    return res.blob();
  },
};

export const fmtLen = (ms: number) => {
  const s = Math.round(ms / 1000), h = Math.floor(s / 3600), m = Math.floor((s % 3600) / 60), x = s % 60;
  const p = (n: number) => String(n).padStart(2, '0');
  return h ? `${h}:${p(m)}:${p(x)}` : `${m}:${p(x)}`;
};
export const fmtDate = (iso: string) => new Date(iso).toLocaleDateString('en-GB', { day: '2-digit', month: 'short', year: 'numeric' });
