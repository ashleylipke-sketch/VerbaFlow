// Whether the signed-in person uses VerbaFlow with a personal account (on their own, not in an organisation).
// Set by App once /api/me has answered, before any page is shown.
let individual = false;
export const isIndividual = () => individual;
export function setIndividual(v: boolean) { individual = v; }
