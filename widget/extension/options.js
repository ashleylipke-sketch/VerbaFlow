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

$('mic').onclick = async () => {
  try {
    const s = await navigator.mediaDevices.getUserMedia({ audio: true });
    s.getTracks().forEach(t => t.stop());
    $('micStatus').textContent = 'The microphone is allowed.'; $('micStatus').className = 'note ok';
  } catch {
    $('micStatus').textContent = 'The microphone was not allowed. Choose Allow when the browser asks, or allow it in the browser’s site settings for this extension.'; $('micStatus').className = 'note bad';
  }
};

navigator.permissions?.query({ name: 'microphone' }).then(p => { if (p.state === 'granted') { $('micStatus').textContent = 'The microphone is allowed.'; $('micStatus').className = 'note ok'; } }).catch(() => {});
fill();
