import { useEffect, useRef, useState } from 'react';
import { api } from './api';

export type CommentPlace = { segmentId: string; from: number; to: number };
export type ReplyRow = { id: string; text: string; by: string; at: string };
export type CommentRow = {
  id: string; quote: string; text: string; by: string; byId: string; at: string; atMs: number; place: CommentPlace | null;
  replies: ReplyRow[]; resolved: boolean; resolvedBy: string | null; resolvedAt: string | null; canRemove: boolean;
};
export type CommentList = { comments: CommentRow[]; canAdd: boolean; why: string | null };
/** Words [from, to) of a passage that a comment is on. */
export type CommentMark = { id: string; from: number; to: number };

const when = (iso: string) => new Date(iso).toLocaleString('en-GB', { day: 'numeric', month: 'short', hour: '2-digit', minute: '2-digit' });

/** Open comments, by passage, for marking their words in the text. Resolved comments and lost places are left out. */
export function commentMarks(list: CommentList | null): Map<string, CommentMark[]> {
  const m = new Map<string, CommentMark[]>();
  for (const c of list?.comments ?? []) {
    if (c.resolved || !c.place) continue;
    const v = m.get(c.place.segmentId) ?? [];
    v.push({ id: c.id, from: c.place.from, to: c.place.to });
    m.set(c.place.segmentId, v);
  }
  return m;
}

/** Open comments first (in the order they were made), then resolved ones. */
export function sortComments(list: CommentRow[]): CommentRow[] {
  return [...list.filter(c => !c.resolved), ...list.filter(c => c.resolved)];
}

/** A selected word on the page: its passage, its position there and its text. */
export type PickedWord = { seg: string; i: number; text: string };

/**
 * The words a selection covers, kept to the passage where it starts (a comment is on words of one passage).
 * Returns null when no word is selected.
 */
export function pickWords(words: PickedWord[]): { segmentId: string; from: number; to: number; quote: string } | null {
  if (words.length === 0) return null;
  const seg = words[0].seg;
  const mine = words.filter(w => w.seg === seg).sort((a, b) => a.i - b.i);
  const from = mine[0].i, to = mine[mine.length - 1].i + 1;
  return { segmentId: seg, from, to, quote: mine.map(w => w.text).join(' ') };
}

const short = (q: string) => q.length > 90 ? q.slice(0, 87).trimEnd() + '…' : q;

/**
 * Comments on words of the text, like margin comments in Word: each shows the words it is on, who wrote it, the replies,
 * a reply box and a Resolved tick. Clicking the words goes to them in the text. New comments are made by selecting words
 * in the text (see ItemPage). Anyone who can open the recording reads them; the people working on it write and resolve.
 */
export default function CommentsCard({ id, list, setList, focus, onShow, onChange }: {
  id: string; list: CommentList | null; setList: (l: CommentList) => void; focus: string | null;
  /** Go to the comment's words in the text (and play from there when there is a recording). */
  onShow: (c: CommentRow) => void; onChange?: () => void;
}) {
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState('');
  const [showResolved, setShowResolved] = useState(false);
  const [replying, setReplying] = useState<string | null>(null);
  const [reply, setReply] = useState('');
  const card = useRef<HTMLDivElement>(null);

  useEffect(() => {
    if (!focus || !list) return;
    const c = list.comments.find(x => x.id === focus);
    if (c?.resolved) setShowResolved(true);
    setTimeout(() => {
      const el = card.current?.querySelector<HTMLElement>(`[data-comment="${CSS.escape(focus)}"]`);
      if (!el) return;
      el.scrollIntoView({ block: 'nearest', behavior: 'smooth' });
      el.classList.add('found');
      setTimeout(() => el.classList.remove('found'), 4000);
    }, 50);
  }, [focus, list]);

  const run = async (fn: () => Promise<CommentList>, after?: () => void) => {
    setBusy(true); setError('');
    try { setList(await fn()); after?.(); onChange?.(); } catch (e: any) { setError(e.message); } finally { setBusy(false); }
  };
  const send = (c: CommentRow) => reply.trim() && run(() => api.post(`/items/${id}/comments/${c.id}/replies`, { text: reply }), () => { setReply(''); setReplying(null); });
  if (!list) return null;
  if (list.comments.length === 0 && !list.canAdd) return null;

  const open = list.comments.filter(c => !c.resolved).length;
  const resolved = list.comments.length - open;
  const shown = sortComments(list.comments).filter(c => showResolved || !c.resolved);
  return (
    <div className="card comments" ref={card}>
      <div className="att-head">
        <h2>Comments{open > 0 && <span className="note"> ({open} open)</span>}</h2>
        {resolved > 0 && <button className="link" onClick={() => setShowResolved(!showResolved)}>{showResolved ? 'Hide' : 'Show'} {resolved} resolved</button>}
      </div>
      {list.canAdd && <p className="note">Select words in the text, then press <b>💬 Comment</b>.</p>}
      {shown.map(c => (
        <div key={c.id} data-comment={c.id} className={'comment' + (c.resolved ? ' resolved' : '')}>
          <button className="link quote" title={c.place ? 'Go to these words' : 'Go to where these words were'} onClick={() => onShow(c)}>“{short(c.quote)}”</button>
          {!c.place && <span className="note"> (these words have since been changed)</span>}
          <div className="comment-text">{c.text}</div>
          <div className="note">{c.by} · {when(c.at)}</div>
          {c.replies.map(r => <div key={r.id} className="reply"><div className="comment-text">{r.text}</div><div className="note">{r.by} · {when(r.at)}</div></div>)}
          {c.resolved && <div className="note">Resolved by {c.resolvedBy}{c.resolvedAt ? ', ' + when(c.resolvedAt) : ''}</div>}
          {list.canAdd && <div className="actions comment-tools">
            {replying !== c.id && <button className="link" onClick={() => { setReplying(c.id); setReply(''); }}>Reply</button>}
            <label className="note"><input type="checkbox" checked={c.resolved} disabled={busy}
              onChange={e => run(() => api.put(`/items/${id}/comments/${c.id}/resolved`, { resolved: e.target.checked }) as any)} /> Resolved</label>
            {c.canRemove && <button className="link" disabled={busy} onClick={() => run(() => api.del(`/items/${id}/comments/${c.id}`) as any)}>Remove</button>}
          </div>}
          {replying === c.id && <div className="note-add">
            <textarea aria-label="Reply" rows={2} maxLength={2000} autoFocus placeholder="Write a reply…" value={reply}
              onChange={e => setReply(e.target.value)}
              onKeyDown={e => { if (e.key === 'Enter' && (e.ctrlKey || e.metaKey)) { e.preventDefault(); send(c); } if (e.key === 'Escape') setReplying(null); }} />
            <div className="actions"><button className="primary" disabled={busy || !reply.trim()} onClick={() => send(c)}>Reply</button>
              <button onClick={() => setReplying(null)}>Cancel</button></div>
          </div>}
        </div>))}
      {list.comments.length > 0 && shown.length === 0 && <p className="note">All comments are resolved.</p>}
      {!list.canAdd && list.why && <p className="note">{list.why}</p>}
      {error && <div className="error">{error}</div>}
    </div>
  );
}

/**
 * The 💬 Comment button that appears by selected words, and the box to write the comment in.
 * `area` is the part of the page holding the text; words there carry data-cseg (passage) and data-ci (position).
 */
export function CommentPicker({ id, area, onAdded }: {
  id: string; area: React.RefObject<HTMLElement | null>; onAdded: (l: CommentList, newId: string | null) => void;
}) {
  const [pick, setPick] = useState<{ segmentId: string; from: number; to: number; quote: string; x: number; y: number } | null>(null);
  const [writing, setWriting] = useState(false);
  const [text, setText] = useState('');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState('');

  useEffect(() => {
    if (writing) return;
    let timer = 0;
    const look = () => {
      const sel = document.getSelection();
      const root = area.current;
      if (!sel || sel.isCollapsed || sel.rangeCount === 0 || !root) { setPick(null); return; }
      const range = sel.getRangeAt(0);
      if (!root.contains(range.commonAncestorContainer)) { setPick(null); return; }
      const words: PickedWord[] = [];
      root.querySelectorAll<HTMLElement>('[data-cseg]').forEach(el => {
        if (range.intersectsNode(el) && (el.textContent ?? '').trim())
          words.push({ seg: el.dataset.cseg!, i: Number(el.dataset.ci), text: el.textContent!.trim() });
      });
      // A selection that ends just at the start of a word's span "touches" it; keep only words with selected characters.
      const real = words.filter(w => {
        const el = root.querySelector<HTMLElement>(`[data-cseg="${CSS.escape(w.seg)}"][data-ci="${w.i}"]`);
        if (!el?.firstChild) return false;
        const r = document.createRange(); r.selectNodeContents(el);
        return range.compareBoundaryPoints(Range.END_TO_START, r) < 0 && range.compareBoundaryPoints(Range.START_TO_END, r) > 0;
      });
      const p = pickWords(real);
      if (!p) { setPick(null); return; }
      const box = range.getBoundingClientRect();
      // Below the words, or above them near the bottom of the screen; always on screen.
      const y = box.bottom + 46 < window.innerHeight ? box.bottom + 6 : Math.max(8, box.top - 40);
      setPick({ ...p, x: Math.min(Math.max(8, box.left + box.width / 2 - 50), window.innerWidth - 120), y: Math.min(y, window.innerHeight - 44) });
    };
    const later = () => { clearTimeout(timer); timer = window.setTimeout(look, 250); };
    document.addEventListener('selectionchange', later);
    window.addEventListener('scroll', later, true);
    return () => { clearTimeout(timer); document.removeEventListener('selectionchange', later); window.removeEventListener('scroll', later, true); };
  }, [area, writing]);

  if (!pick) return null;
  const save = async () => {
    if (!text.trim()) return;
    setBusy(true); setError('');
    try {
      const l: CommentList = await api.post(`/items/${id}/comments`, { segmentId: pick.segmentId, from: pick.from, to: pick.to, text });
      const mine = l.comments.filter(c => c.place?.segmentId === pick.segmentId && c.text === text.trim()).pop();
      onAdded(l, mine?.id ?? null);
      setText(''); setWriting(false); setPick(null);
      document.getSelection()?.removeAllRanges();
    } catch (e: any) { setError(e.message); } finally { setBusy(false); }
  };
  if (!writing) return (
    <button className="comment-pick" style={{ left: pick.x, top: pick.y }}
      onMouseDown={e => e.preventDefault()} onClick={() => setWriting(true)}>💬 Comment</button>);
  return (
    <div className="comment-box" role="dialog" aria-label="New comment" style={{ left: Math.max(8, Math.min(pick.x, window.innerWidth - 330)), top: Math.max(8, Math.min(pick.y, window.innerHeight - 190)) }}>
      <div className="note">On “{short(pick.quote)}”</div>
      <textarea aria-label="Comment" rows={3} maxLength={2000} autoFocus placeholder="Write a comment…" value={text}
        onChange={e => setText(e.target.value)}
        onKeyDown={e => { if (e.key === 'Enter' && (e.ctrlKey || e.metaKey)) { e.preventDefault(); save(); } if (e.key === 'Escape') { setWriting(false); setPick(null); } }} />
      <div className="actions"><button className="primary" disabled={busy || !text.trim()} onClick={save}>{busy ? 'Saving…' : 'Comment'}</button>
        <button onClick={() => { setWriting(false); setPick(null); setText(''); }}>Cancel</button></div>
      {error && <div className="error">{error}</div>}
    </div>);
}
