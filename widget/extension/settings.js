// Shared by the background worker and the options page.
// Sign-in: with Microsoft sign-in set up on the VerbaFlow server, the Widget signs in with the person's Microsoft
// account (Entra ID) and sends a Microsoft access token. A Google or other personal account is linked through the
// VerbaFlow website instead (linkThroughWebsite). On the owner's computer, while VerbaFlow runs in Development,
// a test user (sent as X-Dev-User) can be picked instead; the server refuses test users anywhere else.
export const DEFAULTS = { server: 'http://localhost:5044', user: 'alice', languages: 'en-GB' };

export async function settings() {
  const s = await chrome.storage.local.get(DEFAULTS);
  return { server: String(s.server || DEFAULTS.server).replace(/\/+$/, ''), user: s.user || DEFAULTS.user, languages: s.languages || DEFAULTS.languages };
}

/** How this VerbaFlow server signs people in (public details only), remembered for 10 minutes. */
let known = { server: '', at: 0, info: null };
export async function signInInfo(server) {
  if (known.server === server && Date.now() - known.at < 10 * 60_000) return known.info;
  let info;
  try { info = await (await fetch(`${server}/api/sign-in`)).json(); }
  catch { throw new Error('VerbaFlow cannot be reached. Check that it is running, and the address in the Widget settings.'); }
  known = { server, at: Date.now(), info };
  return info;
}

// ---------- Microsoft sign-in (authorisation code with PKCE, through the browser's own sign-in window) ----------
// The tokens are kept only in this browser session's memory (chrome.storage.session), never on disk.

const b64url = bytes => btoa(String.fromCharCode(...new Uint8Array(bytes))).replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '');
const random = n => b64url(crypto.getRandomValues(new Uint8Array(n)));
const scopes = ms => `${ms.scope} openid profile offline_access`;

async function tokenRequest(ms, form) {
  const res = await fetch(`${ms.authority}/oauth2/v2.0/token`, { method: 'POST', body: new URLSearchParams({ client_id: ms.clientId, scope: scopes(ms), ...form }) });
  const data = await res.json().catch(() => ({}));
  if (!res.ok || !data.access_token) throw new Error('signin');
  const tokens = { access: data.access_token, refresh: data.refresh_token ?? null, expires: Date.now() + (Number(data.expires_in) || 3600) * 1000, clientId: ms.clientId };
  await chrome.storage.session.set({ tokens });
  return tokens;
}

/**
 * Signs in with Microsoft. Interactive shows Microsoft's sign-in window; otherwise it only works when the browser is
 * already signed in to Microsoft (as when Outlook on the web is open) and fails quietly if not.
 */
export async function signInWithMicrosoft(interactive) {
  const s = await settings();
  const ms = (await signInInfo(s.server)).microsoft;
  if (!ms) throw new Error('Microsoft sign-in is not set up on this VerbaFlow.');
  const verifier = random(48), state = random(16);
  const challenge = b64url(await crypto.subtle.digest('SHA-256', new TextEncoder().encode(verifier)));
  const redirect = chrome.identity.getRedirectURL();
  const url = `${ms.authority}/oauth2/v2.0/authorize?` + new URLSearchParams({
    client_id: ms.clientId, response_type: 'code', redirect_uri: redirect, scope: scopes(ms), state,
    code_challenge: challenge, code_challenge_method: 'S256', response_mode: 'query', ...(interactive ? { prompt: 'select_account' } : { prompt: 'none' }),
  });
  let back;
  try {
    const flow = chrome.identity.launchWebAuthFlow({ url, interactive, abortOnLoadForNonInteractive: true, timeoutMsForNonInteractive: 5000 });
    // A quiet attempt never holds things up for long: without a Microsoft session in the browser it simply fails.
    back = new URL(interactive ? await flow : await Promise.race([flow, new Promise((_, no) => setTimeout(no, 8000))]));
  } catch { throw new Error(interactive ? 'The Microsoft sign-in did not finish. Try again.' : 'signin'); }
  const code = back.searchParams.get('code');
  if (!code || back.searchParams.get('state') !== state) throw new Error(interactive ? 'The Microsoft sign-in did not finish. Try again.' : 'signin');
  quietFailedAt = 0;
  return tokenRequest(ms, { grant_type: 'authorization_code', code, redirect_uri: redirect, code_verifier: verifier });
}

let quietFailedAt = 0;
export async function signOut() { await chrome.storage.session.remove('tokens'); await chrome.storage.local.remove('linked'); }

// ---------- Linked through the VerbaFlow website (Google and other personal accounts) ----------
// The Widget gets a short code, the website (where the person is signed in) agrees, and the Widget then holds
// VerbaFlow's own sign-in, renewed once a day. It is kept in this browser profile so the Widget stays signed in.

/** Starts linking: returns the code to show and opens the website's page for it; resolves when the website agreed. */
export async function linkThroughWebsite(onCode) {
  const s = await settings();
  let start;
  try { start = await (await fetch(`${s.server}/api/widget-link/start`, { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ device: 'browser Widget' }) })).json(); }
  catch { throw new Error('VerbaFlow cannot be reached. Check that it is running, and the address in the Widget settings.'); }
  if (!start?.code) throw new Error(start?.error || 'VerbaFlow could not start linking. Try again in a minute.');
  onCode(start.code);
  chrome.tabs.create({ url: `${s.server}/#/link-widget?code=${encodeURIComponent(start.code)}` });
  const until = Date.now() + start.expiresInSeconds * 1000;
  while (Date.now() < until) {
    await new Promise(r => setTimeout(r, 2000));
    const res = await fetch(`${s.server}/api/widget-link/poll`, { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ secret: start.secret }) }).catch(() => null);
    if (!res || res.status === 202) continue;
    const data = await res.json().catch(() => ({}));
    if (!res.ok || !data.token) throw new Error(data.error || 'The link did not finish. Try again.');
    await chrome.storage.session.remove('tokens');
    await chrome.storage.local.set({ linked: { token: data.token, server: s.server, renewed: Date.now() } });
    return;
  }
  throw new Error('The code ran out before it was agreed on the website. Try again.');
}

/** The linked sign-in for this server, renewed when it is a day old; null if there is none (or it ran out). */
async function linkedToken(server) {
  const { linked } = await chrome.storage.local.get('linked');
  if (!linked || linked.server !== server) return null;
  if (Date.now() - linked.renewed > 24 * 3600_000) {
    const res = await fetch(`${server}/api/sign-in/renew`, { method: 'POST', headers: { Authorization: `Bearer ${linked.token}` } }).catch(() => null);
    if (res?.status === 401) { await chrome.storage.local.remove('linked'); return null; }
    const data = res?.ok ? await res.json().catch(() => null) : null;
    if (data?.token) { linked.token = data.token; linked.renewed = Date.now(); await chrome.storage.local.set({ linked }); }
  }
  return linked.token;
}

/** The sign-in to send with a call to VerbaFlow. Throws a plain sentence when the person needs to sign in. */
export async function authHeaders() {
  const s = await settings();
  const info = await signInInfo(s.server);
  const linked = await linkedToken(s.server);
  if (linked) return { Authorization: `Bearer ${linked}` };
  const ms = info.microsoft;
  if (ms) {
    let { tokens } = await chrome.storage.session.get('tokens');
    if (tokens && tokens.clientId !== ms.clientId) tokens = null;
    try {
      if (!tokens || tokens.expires - Date.now() < 120_000) {
        tokens = tokens?.refresh ? await tokenRequest(ms, { grant_type: 'refresh_token', refresh_token: tokens.refresh }).catch(() => null) : null;
        // After a quiet attempt fails, the next one waits two minutes, so test users and error messages are not slowed down.
        if (!tokens && Date.now() - quietFailedAt > 120_000) {
          tokens = await signInWithMicrosoft(false).catch(() => { quietFailedAt = Date.now(); return null; });
        }
        if (!tokens) throw new Error('signin');
      }
      return { Authorization: `Bearer ${tokens.access}` };
    } catch {
      if (!info.testUsers) throw new Error('Sign in to VerbaFlow first: click Settings, then Sign in with Microsoft.');
    }
  }
  if (info.testUsers) return { 'X-Dev-User': s.user };
  if (info.google || info.personalAccounts) throw new Error('Sign in to VerbaFlow first: click Settings, then Sign in through the VerbaFlow website.');
  throw new Error('Sign-in is not set up on this VerbaFlow yet.');
}

/** Calls the VerbaFlow server. Errors come back as the plain sentence the server wrote for people. */
export async function call(method, path, body) {
  const s = await settings();
  const headers = { 'Content-Type': 'application/json', ...(await authHeaders()) };
  let res;
  try {
    res = await fetch(`${s.server}/api${path}`, { method, headers, body: body === undefined ? undefined : JSON.stringify(body) });
  } catch { throw new Error('VerbaFlow cannot be reached. Check that it is running, and the address in the Widget settings.'); }
  const text = await res.text();
  let data = null; try { data = text ? JSON.parse(text) : null; } catch { /* not JSON */ }
  if (res.status === 401) { await signOut(); }
  if (!res.ok) throw new Error(data?.error || data?.detail || data?.title || 'Something went wrong in VerbaFlow.');
  return data;
}
