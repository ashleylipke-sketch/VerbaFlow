// The Widget's microphone page (hidden). It records only while someone speaks into a box, sends the sound to VerbaFlow
// as Speech mode does (16 kHz, 16-bit mono over a live connection), and passes the text back. A lost connection is made
// again and the sound since the last finished phrase is sent again, so nothing is missed. Nothing is kept after a stop.
const RATE = 16000;

class Downsampler {  // the same as web/src/live.ts
  constructor(from, to = RATE) { this.ratio = from / to; this.buf = new Float32Array(0); this.next = 0; }
  push(input) {
    if (this.ratio === 1) return input.slice();
    const buf = new Float32Array(this.buf.length + input.length);
    buf.set(this.buf); buf.set(input, this.buf.length);
    const half = this.ratio / 2, out = [];
    while (this.next + half < buf.length) {
      const a = Math.max(0, Math.floor(this.next - half)), b = Math.min(buf.length, Math.max(a + 1, Math.floor(this.next + half)));
      let sum = 0; for (let i = a; i < b; i++) sum += buf[i];
      out.push(sum / (b - a));
      this.next += this.ratio;
    }
    const keepFrom = Math.max(0, Math.floor(this.next - half) - 1);
    this.buf = buf.slice(keepFrom); this.next -= keepFrom;
    return Float32Array.from(out);
  }
}
function toPcm16(samples) {
  const out = new DataView(new ArrayBuffer(samples.length * 2));
  for (let i = 0; i < samples.length; i++) { const x = Math.max(-1, Math.min(1, samples[i])); out.setInt16(i * 2, x < 0 ? x * 0x8000 : x * 0x7fff, true); }
  return out.buffer;
}
const retryDelay = attempt => Math.min(10_000, 1000 * 2 ** Math.max(0, attempt));
const toBox = msg => chrome.runtime.sendMessage({ ...msg, to: 'box' }).catch(() => {});

let mic = null;        // { stream, ctx }
let live = null;       // the current listen
let reconnects = 0;

function fromSample(chunks, start) {
  const out = []; let at = 0;
  for (const c of chunks) { if (at + c.length > start) out.push(c.subarray(Math.max(0, start - at))); at += c.length; }
  return out;
}

async function openMic() {
  const stream = await navigator.mediaDevices.getUserMedia({ audio: { channelCount: 1, echoCancellation: false, noiseSuppression: false, autoGainControl: false } });
  const ctx = new AudioContext();
  await ctx.audioWorklet.addModule('worklet.js');
  const src = ctx.createMediaStreamSource(stream);
  const tap = new AudioWorkletNode(ctx, 'vf-tap');
  const silent = ctx.createGain(); silent.gain.value = 0;
  src.connect(tap).connect(silent).connect(ctx.destination);
  const down = new Downsampler(ctx.sampleRate);
  tap.port.onmessage = e => {
    const l = live;
    if (!l || l.stopping) return;
    const x = down.push(e.data);
    if (!x.length) return;
    l.chunks.push(x); l.samples += x.length;
    if (l.ready && l.ws?.readyState === WebSocket.OPEN) l.ws.send(toPcm16(x));
  };
  mic = { stream, ctx };
}

function closeMic() {
  mic?.stream.getTracks().forEach(t => t.stop());
  mic?.ctx.close().catch(() => {});
  mic = null;
}

function closeLive() {
  if (!live) return;
  clearTimeout(live.retry); clearTimeout(live.finishTimer);
  const ws = live.ws; live.ws = null;
  if (ws && ws.readyState <= 1) ws.close();
  live.chunks = []; // nothing heard is kept
  live = null;
}

async function connect(l, firstTicket) {
  if (live !== l || l.finished || l.off) return;
  l.ready = false;
  let ticket = firstTicket;
  try {
    if (!ticket) {
      const r = await fetch(`${l.server}/api/live/${l.liveId}/ticket`, { method: 'POST', headers: { 'X-Dev-User': l.user } });
      if (!r.ok) throw new Error();
      ticket = (await r.json()).ticket;
    }
  } catch { toBox({ type: 'link', link: 'lost' }); return retry(l); }
  const fromMs = l.finalTo;
  const ws = new WebSocket(`${l.server.replace(/^http/, 'ws')}/api/live/${l.liveId}/socket?ticket=${ticket}&fromMs=${fromMs}`);
  ws.binaryType = 'arraybuffer';
  l.ws = ws;
  if (l.connections++ > 0) reconnects++;
  ws.onmessage = ev => {
    if (l.ws !== ws) return;
    const m = JSON.parse(ev.data);
    if (m.type === 'ready') {
      l.attempt = 0;
      toBox({ type: 'link', link: 'live' });
      for (const c of fromSample(l.chunks, Math.round(fromMs * RATE / 1000))) for (let i = 0; i < c.length; i += RATE) ws.send(toPcm16(c.subarray(i, i + RATE)));
      l.ready = true;
      if (l.stopping) ws.send(JSON.stringify({ type: 'end' }));
    } else if (m.type === 'text') {
      l.finalTo = m.finalToMs ?? l.finalTo;
      toBox({ type: 'text', segments: m.segments, pendingWords: m.pendingWords ?? 0 });
    } else if (m.type === 'done') {
      l.finished = true;
      l.finalTo = m.finalToMs ?? l.finalTo;
      toBox({ type: 'text', segments: m.segments, pendingWords: 0 });
      stopped(l, true);
    } else if (m.type === 'error') {
      l.off = true;
      toBox({ type: 'error', message: m.message });
      if (l.stopping) stopped(l, false);
    }
  };
  ws.onclose = () => {
    if (l.ws !== ws) return;
    l.ws = null; l.ready = false;
    if (!l.finished && !l.off) { toBox({ type: 'link', link: 'lost' }); retry(l); }
  };
}

function retry(l) {
  clearTimeout(l.retry);
  if (live !== l || l.finished || l.off) return;
  l.retry = setTimeout(() => connect(l), retryDelay(l.attempt++));
}

function stopped(l, complete) {
  if (l.reported) return;
  l.reported = true;
  clearTimeout(l.finishTimer);
  toBox({ type: 'stopped', complete, spokenMs: Math.round(l.samples * 1000 / RATE), reconnects });
  closeLive();
}

chrome.runtime.onMessage.addListener(msg => {
  if (msg.to !== 'mic') return;
  if (msg.type === 'listen') {
    closeLive();
    const l = { server: msg.server, user: msg.user, liveId: msg.liveId, chunks: [], samples: 0, finalTo: 0, attempt: 0, connections: 0 };
    live = l;
    (async () => {
      try { if (!mic) await openMic(); }
      catch (e) {
        live = null;
        toBox({ type: 'error', fatal: true, message: e?.name === 'NotAllowedError'
          ? 'The Widget is not allowed to use the microphone yet. Open the Widget settings and choose Allow the microphone.'
          : 'The microphone could not be started. Check that one is connected and not in use by another app.' });
        return;
      }
      toBox({ type: 'listening' });
      connect(l, msg.ticket);
    })();
  } else if (msg.type === 'stop') {
    const l = live;
    closeMic();
    if (!l) return;
    l.stopping = true;
    if (l.finished || l.off) return stopped(l, l.finished);
    if (l.ready && l.ws) l.ws.send(JSON.stringify({ type: 'end' }));
    l.finishTimer = setTimeout(() => stopped(l, false), 15_000);
  } else if (msg.type === 'close') {
    closeMic(); closeLive(); reconnects = 0;
  }
});
