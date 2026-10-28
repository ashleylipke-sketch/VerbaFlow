/**
 * While recording on a phone: keeps the screen on (a locked screen or a switched-away app stops the microphone on
 * most phones), and calls `onLost` if the microphone stops anyway (screen locked, a phone call, another app took it),
 * so the page can keep what was recorded up to then instead of losing it. Returns a function that undoes both.
 */
export function keepAwake(stream: MediaStream, onLost: (why: 'hidden' | 'ended') => void): () => void {
  let lock: { release: () => Promise<void>; addEventListener?: (t: 'release', f: () => void) => void } | null = null;
  let done = false;
  const ask = async () => {
    try {
      const wl = (navigator as unknown as { wakeLock?: { request: (t: 'screen') => Promise<typeof lock> } }).wakeLock;
      if (!wl || document.visibilityState !== 'visible') return;
      lock = await wl.request('screen');
      // The phone can take the lock back (for example in battery saving); ask again while still recording.
      lock?.addEventListener?.('release', () => { lock = null; if (!done && document.visibilityState === 'visible') setTimeout(ask, 1000); });
    } catch { /* not offered by this browser, or refused: recording still works while the screen stays on */ }
  };
  const lost = (why: 'hidden' | 'ended') => { if (done) return; done = true; onLost(why); };
  const onEnded = () => lost('ended');
  // If the track itself ends, recording cannot go on. On a phone, hiding the page usually stops the microphone a moment later.
  const onVisible = () => {
    if (document.visibilityState === 'visible') { if (!done) ask(); }
    else if (isPhone()) lost('hidden');
  };
  stream.getAudioTracks().forEach(t => t.addEventListener('ended', onEnded));
  document.addEventListener('visibilitychange', onVisible);
  ask();
  return () => {
    done = true;
    stream.getAudioTracks().forEach(t => t.removeEventListener('ended', onEnded));
    document.removeEventListener('visibilitychange', onVisible);
    lock?.release().catch(() => undefined); lock = null;
  };
}

/** A touch screen with no mouse: a phone or tablet. */
export const isPhone = () => { try { return matchMedia('(pointer: coarse)').matches && !matchMedia('(pointer: fine)').matches; } catch { return false; } };

/** "Tap" on a phone or tablet, "Click" with a mouse. */
export const tapWord = () => (isPhone() ? 'Tap' : 'Click');

/** The sentence shown when recording had to stop. */
export const lostMessage = (why: 'hidden' | 'ended') => why === 'hidden'
  ? 'Recording stopped because the screen locked or you switched to another app. Everything up to then is kept. Keep VerbaFlow open on screen while you record.'
  : 'Recording stopped because the microphone was taken away (for example by a phone call). Everything up to then is kept.';
