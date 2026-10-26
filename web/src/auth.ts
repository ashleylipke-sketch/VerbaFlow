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

/** Runs once before the app shows: learns how to sign in, and finishes a Microsoft sign-in the browser has just come back from. */
export async function startSignIn(): Promise<SignInInfo> {
  try { info = await (await fetch('/api/sign-in')).json(); }
  catch { failure = 'VerbaFlow could not be reached. Check your connection and try again.'; return info; }
  if (!info.testUsers) testUser = '';
  if (info.microsoft) {
    try {
      msal = new PublicClientApplication({
        auth: { clientId: info.microsoft.clientId, authority: info.microsoft.authority, redirectUri: location.origin + '/' },
        cache: { cacheLocation: 'localStorage' },
      });
      await msal.initialize();
      const back = await msal.handleRedirectPromise();
      account = back?.account ?? msal.getAllAccounts()[0] ?? null;
      if (account) msal.setActiveAccount(account);
    } catch {
      failure = 'The Microsoft sign-in did not finish. Try again.';
      account = null;
    }
  }
  return info;
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
  if (account && msal) { const a = account; account = null; await msal.logoutRedirect({ account: a, postLogoutRedirectUri: location.origin + '/' }); return; }
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
