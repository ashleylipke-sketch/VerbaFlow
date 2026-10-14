// VerbaFlow Widget on a web page. When you are in a text box, a small microphone button appears beside it.
// Red: listening, the words appear as you speak. Amber: stopped; check or change the text, then Accept.
// Green: added to the box. A plain box is then locked; double-click it to change it. Never in password or payment boxes.
(() => {
  if (window.__verbaflowWidget) return;
  window.__verbaflowWidget = true;

  const site = location.hostname.replace(/^www\./, '').toLowerCase();
  let blocked = false;
  const send = msg => new Promise(res => {
    try { chrome.runtime.sendMessage({ ...msg, to: 'bg' }, r => res(chrome.runtime.lastError ? { ok: false, error: 'The Widget was updated. Reload this page to use it.' } : r ?? { ok: false })); }
    catch { res({ ok: false, error: 'The Widget was updated. Reload this page to use it.' }); }
  });
  send({ type: 'policy' }).then(r => { blocked = !!r?.blocked?.some(b => site === b || site.endsWith('.' + b)); });

  // ---------- which boxes ----------

  const TEXT_TYPES = new Set(['text', 'search', 'email', 'url', '']);
  const SENSITIVE = /pass(word|code|wd)?|pwd|\bpin\b|card|cc-?(num|number|csc|exp)|cvv|cvc|csc|security.?code|iban|sort.?code|account.?number|expir|one-time|otp/i;

  /** The text box an element belongs to, or null when the Widget must not be used there. */
  function boxOf(el) {
    if (!el || !(el instanceof Element)) return null;
    if (el instanceof HTMLTextAreaElement || (el instanceof HTMLInputElement && TEXT_TYPES.has((el.getAttribute('type') || '').toLowerCase()))) {
      if (el.disabled || (el.readOnly && !el.dataset.vfLocked)) return null;
      return sensitive(el) ? null : el;
    }
    if (el.isContentEditable) {
      let root = el;
      while (root.parentElement && root.parentElement.isContentEditable) root = root.parentElement;
      return sensitive(root) ? null : root;
    }
    return null;
  }
  function sensitive(el) {
    if (el instanceof HTMLInputElement && el.type === 'password') return true;
    const hints = ['autocomplete', 'name', 'id', 'aria-label', 'placeholder', 'inputmode'].map(a => el.getAttribute(a) || '').join(' ');
    if (/cc-|one-time-code|current-password|new-password/i.test(el.getAttribute('autocomplete') || '')) return true;
    if (SENSITIVE.test(hints)) return true;
    const form = el.closest('form');
    return !!form?.querySelector('input[type=password]') && !(el instanceof HTMLTextAreaElement) && !el.isContentEditable;
  }

  // ---------- the button and panel (in a shadow root, so the page's styles do not reach them) ----------

  const host = document.createElement('verbaflow-widget');
  host.style.cssText = 'all:initial; position:fixed; z-index:2147483647; top:0; left:0; width:0; height:0;';
  const root = host.attachShadow({ mode: 'closed' });
  root.innerHTML = `<style>
    :host { all: initial; }
    * { box-sizing: border-box; font-family: "Segoe UI", system-ui, -apple-system, sans-serif; }
    .mic { position: fixed; width: 28px; height: 28px; border-radius: 50%; border: 1px solid #c7ced8; background: #fff; color: #2457c5;
      cursor: pointer; display: none; align-items: center; justify-content: center; box-shadow: 0 1px 4px rgba(0,0,0,.18); padding: 0; }
    .mic:hover, .mic:focus-visible { background: #eef3ff; outline: 2px solid #2457c5; outline-offset: 1px; }
    .mic svg { width: 16px; height: 16px; }
    .panel { position: fixed; width: min(440px, calc(100vw - 16px)); background: #fff; color: #1c2430; border-radius: 10px; display: none;
      box-shadow: 0 6px 24px rgba(0,0,0,.25); border: 1px solid #dfe4ea; border-top: 6px solid #9aa7b5; font-size: 14px; }
    .panel.red { border-top-color: #c62828; } .panel.amber { border-top-color: #d98b00; } .panel.green { border-top-color: #157347; }
    .head { display: flex; align-items: center; gap: 8px; padding: 8px 12px 0; font-weight: 600; }
    .dot { width: 10px; height: 10px; border-radius: 50%; background: #9aa7b5; flex: none; }
    .red .dot { background: #c62828; animation: pulse 1.2s infinite; } .amber .dot { background: #d98b00; } .green .dot { background: #157347; }
    @media (prefers-reduced-motion: reduce) { .red .dot { animation: none; } }
    @keyframes pulse { 50% { opacity: .35; } }
    .note { color: #617080; font-weight: 400; font-size: 12px; margin-left: auto; }
    .text { margin: 8px 12px; min-height: 64px; max-height: 220px; overflow: auto; white-space: pre-wrap; line-height: 1.45; border: 1px solid #dfe4ea; border-radius: 6px; padding: 8px; }
    .text .pending { color: #8a96a3; }
    textarea.text { width: calc(100% - 24px); resize: vertical; font-size: 14px; color: inherit; background: #fff; }
    textarea.text:focus { outline: 2px solid #2457c5; }
    .msg { margin: 0 12px 6px; font-size: 12px; color: #617080; } .msg.bad { color: #b42318; }
    .row { display: flex; gap: 8px; padding: 0 12px 12px; flex-wrap: wrap; }
    button.b { font: inherit; font-size: 13px; padding: 6px 12px; border-radius: 6px; border: 1px solid #c7ced8; background: #fff; color: #1c2430; cursor: pointer; }
    button.b:focus-visible { outline: 2px solid #2457c5; outline-offset: 1px; }
    button.b.primary { background: #2457c5; border-color: #2457c5; color: #fff; }
    button.b.stop { background: #c62828; border-color: #c62828; color: #fff; }
    button.b:disabled { opacity: .5; cursor: default; }
    a { color: #2457c5; cursor: pointer; }
    @media (prefers-color-scheme: dark) {
      .mic, .panel, textarea.text, button.b { background: #1a2028; color: #e7ebf0; border-color: #2c3541; }
      .panel { border-top-width: 6px; } .text { border-color: #2c3541; } .note, .msg { color: #9aa7b5; }
    }
  </style>
  <button class="mic" title="Speak into this box (Alt+Shift+D)" aria-label="Speak into this box with VerbaFlow">
    <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round"><rect x="9" y="3" width="6" height="11" rx="3"/><path d="M5 11a7 7 0 0 0 14 0M12 18v3"/></svg>
  </button>
  <div class="panel" role="dialog" aria-label="VerbaFlow Widget">
    <div class="head"><span class="dot"></span><span class="state"></span><span class="note"></span></div>
    <div class="text" aria-live="polite"></div>
    <textarea class="text" aria-label="Text to add" hidden></textarea>
    <div class="msg" role="status"></div>
    <div class="row"></div>
  </div>`;
  const micBtn = root.querySelector('.mic'), panel = root.querySelector('.panel'), stateEl = root.querySelector('.state'),
    noteEl = root.querySelector('.note'), liveEl = root.querySelector('div.text'), editEl = root.querySelector('textarea'),
    msgEl = root.querySelector('.msg'), rowEl = root.querySelector('.row');
  const attach = () => { if (!host.isConnected) (document.body || document.documentElement).appendChild(host); };

  // ---------- state ----------
  // phase: idle | starting | red (listening) | finishing | amber (check, then Accept) | green
  let box = null, focused = null, phase = 'idle', widgetId = null, range = null, sel = null;
  let prefix = '', segments = [], pending = 0, leadBreak = false, spokenMs = 0, reconnects = 0, amend = false, link = '';

  // ---------- following the focused box ----------

  document.addEventListener('focusin', e => {
    const b = boxOf(e.composedPath()[0]);
    if (b && !blocked) { focused = b; if (phase === 'idle') placeMic(); }
    else if (!host.contains(e.target) && phase === 'idle') hideMic();
  }, true);
  document.addEventListener('focusout', () => setTimeout(() => {
    if (phase !== 'idle' || host.matches(':focus-within')) return;
    if (!boxOf(document.activeElement)) hideMic();
  }, 150), true);
  addEventListener('scroll', () => { place(); }, true);
  addEventListener('resize', () => { place(); });

  function placeMic() {
    if (!focused?.isConnected) return hideMic();
    attach();
    const r = focused.getBoundingClientRect();
    micBtn.style.display = 'flex';
    micBtn.style.left = `${Math.min(innerWidth - 32, Math.max(4, r.right - 32))}px`;
    micBtn.style.top = `${Math.min(innerHeight - 32, Math.max(4, r.top + (r.height < 40 ? (r.height - 28) / 2 : 4)))}px`;
  }
  function hideMic() { micBtn.style.display = 'none'; }
  function place() {
    if (phase === 'idle') { if (micBtn.style.display !== 'none') placeMic(); return; }
    const anchor = box?.isConnected ? box.getBoundingClientRect() : { left: 8, bottom: 8, top: 8, right: 8 };
    const h = panel.offsetHeight || 220, w = panel.offsetWidth || 440;
    const below = anchor.bottom + 6 + h < innerHeight;
    panel.style.left = `${Math.max(8, Math.min(innerWidth - w - 8, anchor.left))}px`;
    panel.style.top = `${below ? anchor.bottom + 6 : Math.max(8, anchor.top - h - 6)}px`;
  }

  // ---------- text ----------

  /** The heard text as paragraphs, with list markers, as the VerbaFlow item page shows them. */
  function paragraphs() {
    const paras = [];
    for (const [i, s] of segments.entries()) {
      const pieces = String(s.text ?? '').split('\n');
      pieces.forEach((p, k) => {
        if (i === 0 && k === 0 || s.newParagraph && k === 0 || k > 0) paras.push({ text: p.trim(), list: k === 0 ? s.list ?? null : null });
        else paras[paras.length - 1].text = (paras[paras.length - 1].text + ' ' + p.trim()).trim();
      });
    }
    let n = 0;
    return paras.filter(p => p.text).map(p => {
      n = p.list === 'number' ? n + 1 : 0;
      return p.list === 'bullet' ? '• ' + p.text : p.list === 'number' ? `${n}. ${p.text}` : p.text;
    });
  }
  const isPlain = () => box instanceof HTMLInputElement;
  const joinParas = ps => ps.join(isPlain() ? ' ' : '\n\n');
  // "new paragraph" said first breaks after the text already there; a comma or colon said first joins it.
  const heard = () => { const h = joinParas(paragraphs()); return h && leadBreak && !isPlain() ? '\n\n' + h : h; };
  const joinsOn = h => /^\s/.test(h) || /^[.,;:!?)\]"”’]/.test(h);
  const fullText = () => {
    const p = prefix.trimEnd(), h = heard();
    if (!h) return p.trim();
    if (!p.trim()) return h;
    return joinsOn(h) || p.endsWith('\n') ? p + h : p + ' ' + h;
  };
  const words = t => t.split(/\s+/).filter(Boolean).length;

  function renderLive() {
    liveEl.textContent = '';
    const text = heard();
    if (prefix.trim()) liveEl.append(prefix.trim() + (joinsOn(text) ? '' : ' '));
    const all = text.split(/(\s+)/);
    let wordsLeft = words(text) - pending, firm = '';
    const out = [];
    for (const piece of all) {
      if (/^\s+$/.test(piece) || !piece) { (wordsLeft > 0 ? (firm += piece) : out.push(piece)); continue; }
      if (wordsLeft-- > 0) firm += piece; else out.push(piece);
    }
    liveEl.append(firm);
    if (out.length) { const g = document.createElement('span'); g.className = 'pending'; g.textContent = out.join(''); liveEl.append(g); }
    if (!text && !prefix.trim()) { const g = document.createElement('span'); g.className = 'pending'; g.textContent = 'Start speaking…'; liveEl.append(g); }
    liveEl.scrollTop = liveEl.scrollHeight;
  }

  // ---------- the panel ----------

  function show(newPhase, message = '', bad = false) {
    phase = newPhase;
    attach(); hideMic();
    panel.style.display = phase === 'idle' ? 'none' : 'block';
    panel.className = 'panel ' + ({ red: 'red', finishing: 'red', amber: 'amber', green: 'green' }[phase] || '');
    stateEl.textContent = { starting: 'Starting…', red: 'Listening', finishing: 'Finishing…', amber: 'Check the text, then Accept', green: 'Added' }[phase] || '';
    noteEl.textContent = phase === 'red' ? (link === 'lost' ? 'Connection lost: still recording' : 'Alt+Shift+D to stop') : phase === 'amber' ? 'Ctrl+Enter to accept' : '';
    msgEl.textContent = message; msgEl.className = 'msg' + (bad ? ' bad' : '');
    const editing = phase === 'amber';
    liveEl.hidden = editing; editEl.hidden = !editing;
    rowEl.textContent = '';
    const button = (label, cls, fn, disabled = false) => {
      const b = document.createElement('button'); b.className = 'b ' + cls; b.textContent = label; b.disabled = disabled; b.onclick = fn; rowEl.append(b); return b;
    };
    if (phase === 'starting') button('Cancel', '', cancel);
    if (phase === 'red') { button('Stop', 'stop', stop).focus(); button('Cancel', '', cancel); }
    if (phase === 'finishing') button('Cancel', '', cancel);
    if (phase === 'amber') {
      button('Accept', 'primary', accept, !editEl.value.trim() && !amend);
      button('Speak more', '', speakMore);
      button('Cancel', '', cancel);
      const s = document.createElement('a'); s.textContent = 'Settings'; s.style.cssText = 'margin-left:auto;align-self:center;font-size:12px'; s.onclick = () => send({ type: 'settings' }); rowEl.append(s);
    }
    if (phase === 'red' || phase === 'finishing' || phase === 'starting') renderLive();
    place();
  }

  editEl.addEventListener('input', () => { const a = rowEl.querySelector('button.primary'); if (a) a.disabled = !editEl.value.trim() && !amend; });
  panel.addEventListener('keydown', e => {
    if (e.key === 'Escape') { e.preventDefault(); cancel(); }
    else if (e.key === 'Enter' && (e.ctrlKey || e.metaKey) && phase === 'amber') { e.preventDefault(); accept(); }
    e.stopPropagation();
  });

  // ---------- starting, stopping, accepting ----------

  async function start(withPrefix = '') {
    if (blocked || !focused) return;
    box = focused;
    if (box.dataset.vfLocked) unlock(box);
    // Where the text goes: the cursor or the selected part of the box. Selected text is changed (amended).
    if (box instanceof HTMLInputElement || box instanceof HTMLTextAreaElement) {
      sel = [box.selectionStart ?? box.value.length, box.selectionEnd ?? box.value.length];
      if (!withPrefix && sel[1] > sel[0]) withPrefix = box.value.slice(sel[0], sel[1]);
    } else {
      const s = getSelection();
      range = s && s.rangeCount && box.contains(s.getRangeAt(0).commonAncestorContainer) ? s.getRangeAt(0).cloneRange() : null;
      if (!withPrefix && range && !range.collapsed) withPrefix = range.toString();
    }
    amend = !!withPrefix;
    prefix = withPrefix; segments = []; pending = 0; leadBreak = false; spokenMs = 0; reconnects = 0; link = '';
    show('starting');
    const r = await send({ type: 'start', site: location.hostname });
    if (!r.ok) { editEl.value = prefix; show('amber', r.error || 'The Widget could not start.', true); return; }
    widgetId = r.widgetId;
  }

  function stop() { if (phase !== 'red') return; show('finishing'); send({ type: 'stop', widgetId }); }

  function speakMore() {
    prefix = editEl.value; segments = []; pending = 0; leadBreak = false; link = '';
    show('starting');
    if (!widgetId) return start(prefix);
    send({ type: 'again', widgetId }).then(r => { if (!r.ok) show('amber', r.error, true); });
  }

  function cancel() {
    if (widgetId) send({ type: 'end', widgetId, outcome: 'cancelled', counts: { spokenMs, reconnects } });
    if (amend && box?.dataset.vfAmending) { lock(box); delete box.dataset.vfAmending; }
    reset();
  }

  function reset() {
    // The Widget holds nothing once a dictation is accepted or cancelled.
    widgetId = null; prefix = ''; segments = []; pending = 0; leadBreak = false; editEl.value = ''; liveEl.textContent = ''; range = null; sel = null; amend = false;
    phase = 'idle'; panel.style.display = 'none';
    const b = box; box = null;
    if (b?.isConnected) { b.focus({ preventScroll: true }); }
  }

  function accept() {
    const text = editEl.value.replace(/\s+$/, '');
    const b = box;
    if (!b?.isConnected) { show('amber', 'The text box is no longer on the page. Copy the text from here instead.', true); return; }
    insert(b, text);
    send({ type: 'end', widgetId, outcome: 'accepted', counts: { spokenMs, words: words(text), reconnects } });
    widgetId = null;
    if (b instanceof HTMLInputElement || b instanceof HTMLTextAreaElement) lock(b);
    else { b.style.outline = '2px solid #157347'; setTimeout(() => { b.style.outline = ''; }, 1500); }
    delete b.dataset.vfAmending;
    show('green');
    setTimeout(() => { if (phase === 'green') reset(); }, 900);
  }

  function insert(b, text) {
    b.focus({ preventScroll: true });
    if (b instanceof HTMLInputElement || b instanceof HTMLTextAreaElement) {
      if (b.dataset.vfAmending) sel = [0, b.value.length];
      const [s, e] = sel ?? [b.value.length, b.value.length];
      b.setSelectionRange(s, e);
      text = spaced(b.value.slice(0, s), text);
      // insertText keeps the page's own undo and tells web apps the text changed, as typing would.
      if (!document.execCommand('insertText', false, text)) { b.setRangeText(text, s, e, 'end'); b.dispatchEvent(new Event('input', { bubbles: true })); }
      return;
    }
    const s = getSelection();
    if (range) { s.removeAllRanges(); s.addRange(range); }
    const r = s.rangeCount ? s.getRangeAt(0) : null;
    if (r && r.startContainer.nodeType === Node.TEXT_NODE) text = spaced(r.startContainer.data.slice(0, r.startOffset), text);
    const paras = text.split(/\n{2,}/);
    paras.forEach((p, i) => {
      if (i > 0) document.execCommand('insertParagraph');
      p.split('\n').forEach((line, k) => { if (k > 0) document.execCommand('insertLineBreak'); if (line) document.execCommand('insertText', false, line); });
    });
  }

  // A space between what was already there and the new words, as a person typing would leave one.
  function spaced(before, text) {
    return before && !/\s$/.test(before) && !/^\s/.test(text) && !/^[.,;:!?)\]"”’]/.test(text) ? ' ' + text : text;
  }

  // A plain box the Widget filled is locked, so it is not changed by accident; double-click it to change it.
  function lock(b) {
    b.readOnly = true; b.dataset.vfLocked = '1';
    b.style.outline = '2px solid #157347'; b.style.outlineOffset = '1px';
    b.title = 'Added with VerbaFlow Widget. Double-click to change it.';
  }
  function unlock(b) {
    b.readOnly = false; delete b.dataset.vfLocked;
    b.style.outline = ''; b.style.outlineOffset = ''; b.removeAttribute('title');
  }
  document.addEventListener('dblclick', e => {
    const b = e.composedPath()[0];
    if (!(b instanceof HTMLElement) || !b.dataset.vfLocked || phase !== 'idle') return;
    e.preventDefault();
    unlock(b);
    b.dataset.vfAmending = '1';
    focused = b; box = b; amend = true; sel = [0, b.value.length]; widgetId = null; prefix = '';
    editEl.value = b.value;
    show('amber', 'Change the text, or choose Speak more to add to it.');
    editEl.focus();
  }, true);

  micBtn.addEventListener('mousedown', e => e.preventDefault()); // keeps the cursor where it is in the box
  micBtn.addEventListener('click', () => start());

  // ---------- messages from the Widget ----------

  chrome.runtime.onMessage.addListener(msg => {
    if (msg.type === 'toggle') {
      if (phase === 'red') stop();
      else if (phase === 'idle' && document.hasFocus() && focused && document.activeElement && boxOf(document.activeElement) === focused) start();
      return;
    }
    if (phase === 'starting' && !widgetId) widgetId = msg.widgetId; // the first words can arrive before the start is confirmed
    if (!widgetId || msg.widgetId !== widgetId) return;
    if (msg.type === 'listening') { show('red'); }
    else if (msg.type === 'link') { link = msg.link; if (phase === 'red') noteEl.textContent = link === 'lost' ? 'Connection lost: still recording' : 'Alt+Shift+D to stop'; }
    else if (msg.type === 'text') { segments = msg.segments ?? []; pending = msg.pendingWords ?? 0; leadBreak = !!msg.startsWithBreak; if (phase === 'red' || phase === 'finishing') renderLive(); }
    else if (msg.type === 'stopped') {
      spokenMs += msg.spokenMs ?? 0; reconnects = msg.reconnects ?? reconnects; pending = 0;
      editEl.value = fullText();
      show('amber', msg.complete ? '' : 'The connection did not come back, so the last words may be missing. Check the text, or Speak more.', !msg.complete);
      editEl.focus(); editEl.setSelectionRange(editEl.value.length, editEl.value.length);
    }
    else if (msg.type === 'error') {
      if (msg.fatal) { editEl.value = prefix; show('amber', msg.message, true); }
      else { msgEl.textContent = msg.message; msgEl.className = 'msg bad'; }
    }
  });

  addEventListener('pagehide', () => { if (widgetId) send({ type: 'end', widgetId, outcome: 'cancelled', counts: { spokenMs, reconnects } }); });
})();
