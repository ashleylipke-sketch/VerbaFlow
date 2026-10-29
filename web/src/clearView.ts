/** Bottom edge of whatever stays pinned at the top of the page (the header, and the player on an item page). */
export function coveredTop(): number {
  // The player is pinned just under the header, so it covers the screen when its top meets the header's bottom.
  let bottom = 0;
  document.querySelectorAll<HTMLElement>('header.top, .player').forEach(el => {
    const r = el.getBoundingClientRect();
    if (r.height > 0 && r.top <= bottom + 1) bottom = Math.max(bottom, r.bottom);
  });
  return bottom;
}

/**
 * Scrolls so the element sits in the part of the screen the pinned header and player do not cover: centred when it
 * fits, else with its top just below the player. Does nothing when it is already fully in that part. A box that fits sits a third of the way down, leaving room for what comes next.
 */
export function scrollIntoClearView(el: HTMLElement | null, onlyIfHidden = false) {
  if (!el) return;
  const top = coveredTop() + 8;
  const room = window.innerHeight - top;
  const r = el.getBoundingClientRect();
  if (onlyIfHidden && r.top >= top && r.bottom <= window.innerHeight) return;
  const target = r.height < room ? top + (room - r.height) / 3 : top;
  const calm = window.matchMedia('(prefers-reduced-motion: reduce)').matches;
  window.scrollBy({ top: r.top - target, behavior: calm ? 'auto' : 'smooth' });
}
