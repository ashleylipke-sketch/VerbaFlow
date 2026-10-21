// The Widget's coordinator. A text box on a page (content.js) asks to start; this worker opens the session on the
// VerbaFlow server, starts the microphone page (offscreen.js) and passes the live text back to the box.
// One box at a time. Nothing heard is kept here.
import { authHeaders, call, settings } from './settings.js';

let active = null; // { tabId, frameId, widgetId }
let policy = { at: 0, blocked: [] };

async function ensureOffscreen() {
  if (await chrome.offscreen.hasDocument()) return;
  await chrome.offscreen.createDocument({ url: 'offscreen.html', reasons: ['USER_MEDIA'], justification: 'Hears the microphone while you speak into a text box.' });
}

const toBox = msg => active && chrome.tabs.sendMessage(active.tabId, { ...msg, widgetId: active.widgetId }, { frameId: active.frameId }).catch(() => {});
const toMic = msg => chrome.runtime.sendMessage({ ...msg, to: 'mic' }).catch(() => {});

function device() {
  const ua = navigator.userAgent;
  const browser = /Edg\//.test(ua) ? 'Edge' : /OPR\//.test(ua) ? 'Opera' : /Chrome\//.test(ua) ? 'Chrome' : 'Browser';
  const system = /Windows/.test(ua) ? 'Windows' : /Mac OS/.test(ua) ? 'Mac' : /CrOS/.test(ua) ? 'ChromeOS' : /Linux/.test(ua) ? 'Linux' : 'unknown system';
  return `${browser} on ${system}`;
}

async function begin(sender, site) {
  if (active) await finish('cancelled', {});
  const s = await settings();
  const r = await call('POST', '/widget/start', { site, device: device(), spokenLanguages: s.languages });
  active = { tabId: sender.tab.id, frameId: sender.frameId, widgetId: r.id };
  await ensureOffscreen();
  toMic({ type: 'listen', server: s.server, liveId: r.liveId, ticket: r.ticket });
  return { widgetId: r.id };
}

async function again() {
  const r = await call('POST', `/widget/${active.widgetId}/listen-again`);
  const s = await settings();
  await ensureOffscreen();
  toMic({ type: 'listen', server: s.server, liveId: r.liveId, ticket: r.ticket });
}

async function finish(outcome, counts) {
  if (!active) return;
  const id = active.widgetId;
  active = null;
  toMic({ type: 'close' });
  try { await call('POST', `/widget/${id}/end`, { outcome, ...counts }); } catch { /* the session is forgotten by the server in time anyway */ }
}

chrome.runtime.onMessage.addListener((msg, sender, reply) => {
  if (msg.to === 'box') { toBox(msg); return; }   // from the microphone page
  if (msg.to !== 'bg') return;
  const run = async () => {
    switch (msg.type) {
      case 'policy': {
        if (Date.now() - policy.at > 5 * 60_000) {
          try { const p = await call('GET', '/widget/policy'); policy = { at: Date.now(), blocked: p.blockedSites ?? [] }; }
          catch { policy = { at: Date.now(), blocked: policy.blocked }; }
        }
        return { blocked: policy.blocked };
      }
      case 'start': return await begin(sender, msg.site);
      case 'auth-headers': return { headers: await authHeaders() }; // for the microphone page's reconnects
      case 'again': if (!active || active.widgetId !== msg.widgetId) throw new Error('This dictation has ended.'); await again(); return {};
      case 'grammar': {
        if (!active || active.widgetId !== msg.widgetId) throw new Error('This dictation has ended.');
        return await call('POST', `/widget/${msg.widgetId}/grammar`, { text: msg.text });
      }
      case 'stop': if (active?.widgetId === msg.widgetId) toMic({ type: 'stop' }); return {};
      case 'end': if (active?.widgetId === msg.widgetId) await finish(msg.outcome, msg.counts ?? {}); return {};
      case 'settings': chrome.runtime.openOptionsPage(); return {};
      case 'mic-test': await ensureOffscreen(); return (await chrome.runtime.sendMessage({ to: 'mic', type: 'test' })) ?? { ok: false };
      case 'mic-setting': {
        // The browser's own page where the microphone can be allowed for the Widget for good.
        const scheme = /Edg\//.test(navigator.userAgent) ? 'edge' : 'chrome';
        await chrome.tabs.create({ url: `${scheme}://settings/content/siteDetails?site=${encodeURIComponent(`chrome-extension://${chrome.runtime.id}`)}` });
        return {};
      }
    }
    return {};
  };
  run().then(r => reply({ ok: true, ...r }), e => reply({ ok: false, error: e.message }));
  return true;
});

chrome.commands.onCommand.addListener(async command => {
  if (command !== 'toggle-widget') return;
  const [tab] = await chrome.tabs.query({ active: true, currentWindow: true });
  if (tab?.id) chrome.tabs.sendMessage(tab.id, { type: 'toggle' }).catch(() => {});
});

chrome.tabs.onRemoved.addListener(tabId => { if (active?.tabId === tabId) finish('cancelled', {}); });
chrome.action.onClicked.addListener(() => chrome.runtime.openOptionsPage());
// The settings open once when the extension is added, to allow the microphone and connect to VerbaFlow.
chrome.runtime.onInstalled.addListener(d => { if (d.reason === 'install') chrome.runtime.openOptionsPage(); });
