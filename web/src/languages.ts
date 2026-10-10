const legacy: Record<string, string> = { en: 'en-GB', fr: 'fr-FR', es: 'es-ES', de: 'de-DE', it: 'it-IT', nl: 'nl-NL' };

/** The code stored for an older choice ("en") is shown as the matching full language ("en-GB"). */
export const parseSpoken = (value: string): string[] =>
  value.split(',').map(x => x.trim()).filter(Boolean).map(x => legacy[x.toLowerCase()] ?? x);

/** The language the summary is written in: the spoken language when there is exactly one, otherwise English. */
export const summaryLanguage = (value: string): string => {
  const p = parseSpoken(value);
  return p.length === 1 && p[0] !== 'auto' ? p[0].split('-')[0] : 'en';
};

