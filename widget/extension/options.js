import { DEFAULTS, settings } from './settings.js';

const $ = id => document.getElementById(id);

async function fill() {
  const s = await settings();
  $('server').value = s.server;
  try {
    const headers = { 'X-Dev-User': s.user };
    const [users, langs] = await Promise.all([
      fetch(`${s.server}/api/users`, { headers }).then(r => r.json()),
      fetch(`${s.server}/api/languages`, { headers }).then(r => r.json()),
    ]);
    $('user').innerHTML = users.map(u => {
      const key = u.email.split('@')[0];
      return `<option value="${key}"${key === s.user ? ' selected' : ''}>${u.name}${u.isAdmin ? ' (admin)' : ''}</option>`;
    }).join('');
    $('languages').innerHTML = langs.languages.filter(l => l.supported !== false && l.code !== 'auto')
      .map(l => `<option value="${l.code}"${l.code === s.languages ? ' selected' : ''}>${l.name}</option>`).join('');
    $('status').textContent = 'Connected to VerbaFlow.'; $('status').className = 'note ok';
  } catch {
    $('user').innerHTML = `<option value="${s.user}">${s.user}</option>`;
    $('languages').innerHTML = `<option value="${s.languages}">${s.languages}</option>`;
    $('status').textContent = 'VerbaFlow cannot be reached at this address. Check that it is running (run.cmd).'; $('status').className = 'note bad';
  }
}

$('save').onclick = async () => {
  await chrome.storage.local.set({
    server: $('server').value.trim() || DEFAULTS.server, user: $('user').value || DEFAULTS.user, languages: $('languages').value || DEFAULTS.languages,
  });
  await fill();
};

const micNote = (text, ok) => { $('micStatus').textContent = text; $('micStatus').className = `note ${ok ? 'ok' : 'bad'}`; };
const bg = msg => chrome.runtime.sendMessage({ ...msg, to: 'bg' });

// The settings page may be allowed while the hidden page that does the listening is not (for example when the
// browser was told "Allow this time"), so the check is made from there.
async function checkMic() {
  const r = await bg({ type: 'mic-test' }).catch(() => null);
  if (r?.ok) { micNote('The microphone is allowed. The Widget can hear you.', true); $('micFix').hidden = true; return true; }
  micNote('The browser allowed the microphone only for now, so the Widget cannot hear you yet. Click Open the microphone setting, set Microphone to Allow, then come back and click Allow the microphone again.', false);
  $('micFix').hidden = false;
  return false;
}

$('mic').onclick = async () => {
  try {
    const s = await navigator.mediaDevices.getUserMedia({ audio: true });
    s.getTracks().forEach(t => t.stop());
  } catch {
    micNote('The microphone was not allowed. Click Allow the microphone again and choose Allow, or click Open the microphone setting and set Microphone to Allow.', false);
    $('micFix').hidden = false;
    return;
  }
  await checkMic();
};
$('micFix').onclick = async () => {
  const r = await bg({ type: 'mic-setting' }).catch(() => null);
  if (!r?.ok) micNote('Open Settings in the browser, then Cookies and site permissions (Edge) or Privacy and security (Chrome), Microphone, and allow VerbaFlow Widget.', false);
};

navigator.permissions?.query({ name: 'microphone' }).then(p => { if (p.state === 'granted') checkMic(); }).catch(() => {});
fill();
