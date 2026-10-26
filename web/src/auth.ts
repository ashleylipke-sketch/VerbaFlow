// Signing in. With Microsoft sign-in set up on the server, people sign in with their Microsoft work account (Entra ID)
// and every call carries a Microsoft access token. On the owner's computer (Development) the test users (alice, bob,
// carol...) can still be picked, sent as the X-Dev-User header; the server refuses them anywhere else.
import { InteractionRequiredAuthError, PublicClientApplication, type AccountInfo } from '@azure/msal-browser';

export type SignInInfo = {
  microsoft: { tenantId: string; clientId: string; scope: string; authority: string } | null;
  testUsers: boolean;
};

let info: SignInInfo = { microsoft: null, testUsers: false };
let msal: PublicClientApplication | null = null;
let account: AccountInfo | null = null;
let testUser = read('devUser');
let failure = '';
let redirecting = false;

function read(key: string) { try { return localStorage.getItem(key) ?? ''; } catch { return ''; } }
function write(key: string, value: string) { try { if (value) localStorage.setItem(key, value); else localStorage.removeItem(key); } catch { /* private window */ } }

/**
 * Where Microsoft sends the browser back to: a path of its own, registered as a Single-page application. Microsoft
 * ignores the port of a localhost address, so plain http://localhost:5044/ would clash with the Widget for Windows'
 * http://localhost (a desktop app), and Microsoft then refused the sign-in (AADSTS9002326). The path keeps them apart.
 */
const RETURN_PATH = '/signin';
const returnAddress = () => location.origin + RETURN_PATH;

/** Runs once before the app shows: learns how to sign in, and finishes a Microsoft sign-in the browser has just come back from. */
export async function startSignIn(): Promise<SignInInfo> {
  try { info = await (await fetch('/api/sign-in')).json(); }
  catch { failure = 'VerbaFlow could not be reached. Check your connection and try again.'; return info; }
  if (!info.testUsers) testUser = '';
  if (info.microsoft) {
    try {
      msal = new PublicClientApplication({
        auth: { clientId: info.microsoft.clientId, authority: info.microsoft.authority, redirectUri: returnAddress() },
        cache: { cacheLocation: 'localStorage' },
      });
      await msal.initialize();
      const back = await msal.handleRedirectPromise();
      account = back?.account ?? msal.getAllAccounts()[0] ?? null;
      if (account) msal.setActiveAccount(account);
      if (location.pathname === RETURN_PATH) history.replaceState(null, '', '/' + location.hash);
    } catch (e) {
      // Something left over from an earlier sign-in (half-finished, or from an older version) can make every return
      // from Microsoft fail. It is cleared, so the next Sign in with Microsoft starts afresh, and the reason is
      // recorded for support under a reference.
      account = null;
      forgetMicrosoftState(info.microsoft.clientId, info.microsoft.tenantId);
      const ref = await reportProblem(e);
      failure = `The Microsoft sign-in did not finish. Click Sign in with Microsoft to try again.${ref ? ` Reference: ${ref}.` : ''}`;
    }
  }
  return info;
}

/** Removes the Microsoft sign-in's stored state in this browser (tokens, half-finished sign-ins), never anything else. */
function forgetMicrosoftState(clientId: string, tenantId: string) {
  for (const store of [localStorage, sessionStorage]) {
    try {
      const keys: string[] = [];
      for (let i = 0; i < store.length; i++) { const k = store.key(i); if (k) keys.push(k); }
      for (const k of keys) if (k.startsWith('msal.') || k.includes(clientId) || k.includes(tenantId)) store.removeItem(k);
    } catch { /* storage blocked */ }
  }
  try { document.cookie = 'msal.cache.encryption=; expires=Thu, 01 Jan 1970 00:00:00 GMT; path=/'; } catch { /* no cookies */ }
}

/** Sends what went wrong to VerbaFlow's support record and returns the reference to show, if VerbaFlow could be reached. */
async function reportProblem(e: unknown): Promise<string> {
  const x = e as { errorCode?: string; errorMessage?: string; message?: string; name?: string };
  try {
    const r = await fetch('/api/sign-in/problem', { method: 'POST', headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ code: String(x?.errorCode ?? x?.name ?? 'unknown'), detail: String(x?.errorMessage ?? x?.message ?? e) }) });
    return r.ok ? (await r.json()).reference ?? '' : '';
  } catch { return ''; }
}

export const signInInfo = () => info;
export const signInProblem = () => failure;
/** How the person is signed in now, if at all. */
export const signedIn = (): 'microsoft' | 'test' | null => account ? 'microsoft' : testUser ? 'test' : null;
export const microsoftName = () => account?.name || account?.username || '';
export const getTestUser = () => testUser;
export function setTestUser(u: string) { testUser = info.testUsers ? u : ''; write('devUser', testUser); }

export async function signInWithMicrosoft() {
  if (!msal || !info.microsoft) return;
  testUser = ''; write('devUser', '');
  await msal.loginRedirect({ scopes: [info.microsoft.scope], prompt: 'select_account' });
}

export async function signOut() {
  if (account && msal) { const a = account; account = null; await msal.logoutRedirect({ account: a, postLogoutRedirectUri: returnAddress() }); return; }
  setTestUser('');
}

/** The sign-in to send with a call to VerbaFlow. */
export async function authHeaders(): Promise<Record<string, string>> {
  if (account && msal && info.microsoft) {
    try {
      const t = await msal.acquireTokenSilent({ scopes: [info.microsoft.scope], account });
      return { Authorization: `Bearer ${t.accessToken}` };
    } catch (e) {
      // Microsoft wants the person to sign in again (after a day or so, or when the quiet renewal is blocked by the
      // browser): go to Microsoft once and come back here. Several calls can fail at the same moment; only one goes.
      if (!redirecting) {
        redirecting = true;
        try { await msal.acquireTokenRedirect({ scopes: [info.microsoft.scope], account }); }
        catch { redirecting = false; }
      }
      throw new Error(e instanceof InteractionRequiredAuthError || redirecting ? 'Taking you to Microsoft to sign in again…' : 'Your sign-in has run out. Sign in again.');
    }
  }
  return testUser ? { 'X-Dev-User': testUser } : {};
}
