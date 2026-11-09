import { DEFAULTS, call, linkThroughWebsite, settings, signInInfo, signInWithMicrosoft, signOut } from './settings.js';

const $ = id => document.getElementById(id);
const status = (text, ok) => { $('status').textContent = text; $('status').className = `note ${ok ? 'ok' : 'bad'}`; };

async function fill() {
  const s = await settings();
  $('server').value = s.server;
  let info;
  try { info = await signInInfo(s.server); }
  catch {
    $('msBox').hidden = true; $('testBox').hidden = true; $('linkBox').hidden = true;
    $('languages').innerHTML = `<option value="${s.languages}">${s.languages}</option>`;
    return status('VerbaFlow cannot be reached at this address. Check that it is running (run.cmd).', false);
  }
  $('msBox').hidden = !info.microsoft;
  $('linkBox').hidden = !(info.google || info.personalAccounts);
  $('testBox').hidden = !info.testUsers;
  if (info.testUsers) {
    const users = await fetch(`${s.server}/api/users`, { headers: { 'X-Dev-User': s.user } }).then(r => r.json()).catch(() => []);
    $('user').innerHTML = users.filter(u => u.email.endsWith('@example.test')).map(u => {
      const key = u.email.split('@')[0];
      return `<option value="${key}"${key === s.user ? ' selected' : ''}>${u.name}${u.isAdmin ? ' (admin)' : ''}</option>`;
    }).join('');
  }
  let me = null;
  try { me = await call('GET', '/me'); } catch (e) { me = null; if (!info.microsoft && !info.google && !info.personalAccounts) return status(e.message, false); }
  const signed = me && !me.email?.endsWith('@example.test');
  const { linked } = await chrome.storage.local.get('linked');
  const viaLink = signed && linked?.server === s.server;
  const who = signed ? `Signed in as ${me.name}${me.isAdmin ? ' (admin)' : ''}${me.individual ? ' (personal account)' : ''}.` : 'Not signed in yet.';
  if (info.microsoft) { $('who').textContent = viaLink ? '' : who; $('msIn').hidden = !!signed; $('msOut').hidden = !signed || viaLink; }
  $('linkWho').textContent = viaLink ? who : '';
  $('linkIn').hidden = !!signed; $('linkOut').hidden = !viaLink;
  if (!me) { $('languages').innerHTML = `<option value="${s.languages}">${s.languages}</option>`; return status('Sign in to use the Widget.', false); }
  const langs = await call('GET', '/languages');
  $('languages').innerHTML = langs.languages.filter(l => l.supported !== false && l.code !== 'auto')
    .map(l => `<option value="${l.code}"${l.code === s.languages ? ' selected' : ''}>${l.name}</option>`).join('');
  status(`Connected to VerbaFlow as ${me.name}.`, true);
}

$('save').onclick = async () => {
  await chrome.storage.local.set({
    server: $('server').value.trim() || DEFAULTS.server, user: $('user').value || DEFAULTS.user, languages: $('languages').value || DEFAULTS.languages,
  });
  await fill();
};
$('msIn').onclick = async () => {
  try { await chrome.storage.local.set({ server: $('server').value.trim() || DEFAULTS.server }); await signInWithMicrosoft(true); }
  catch (e) { return status(e.message === 'signin' ? 'The Microsoft sign-in did not finish. Try again.' : e.message, false); }
  await fill();
};
$('msOut').onclick = async () => { await signOut(true); await fill(); };
$('linkIn').onclick = async () => {
  $('linkCode').textContent = '';
  try {
    await chrome.storage.local.set({ server: $('server').value.trim() || DEFAULTS.server });
    await linkThroughWebsite(code => {
      $('linkCode').innerHTML = `A VerbaFlow tab has opened. Check it shows the code <strong>${code}</strong>, then click <strong>Link it</strong> there. (Not signed in? Sign in on that page first: the code stays the same.)`;
    });
    $('linkCode').textContent = '';
  } catch (e) { $('linkCode').textContent = ''; return status(e.message, false); }
  await fill();
};
$('linkOut').onclick = async () => { await signOut(true); await fill(); };

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
