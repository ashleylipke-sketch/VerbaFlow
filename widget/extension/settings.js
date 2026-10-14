// Shared by the background worker, the options page and the microphone page.
// The sign-in is the development stand-in (a user name sent as X-Dev-User), as in the VerbaFlow app itself.
export const DEFAULTS = { server: 'http://localhost:5044', user: 'alice', languages: 'en-GB' };

export async function settings() {
  const s = await chrome.storage.local.get(DEFAULTS);
  return { server: String(s.server || DEFAULTS.server).replace(/\/+$/, ''), user: s.user || DEFAULTS.user, languages: s.languages || DEFAULTS.languages };
}

/** Calls the VerbaFlow server. Errors come back as the plain sentence the server wrote for people. */
export async function call(method, path, body) {
  const s = await settings();
  let res;
  try {
    res = await fetch(`${s.server}/api${path}`, {
      method, headers: { 'Content-Type': 'application/json', 'X-Dev-User': s.user },
      body: body === undefined ? undefined : JSON.stringify(body),
    });
  } catch { throw new Error('VerbaFlow cannot be reached. Check that it is running, and the address in the Widget settings.'); }
  const text = await res.text();
  let data = null; try { data = text ? JSON.parse(text) : null; } catch { /* not JSON */ }
  if (!res.ok) throw new Error(data?.error || data?.detail || data?.title || 'Something went wrong in VerbaFlow.');
  return data;
}
