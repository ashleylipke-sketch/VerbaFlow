/** The spoken commands a dictation understands (kept in step with DictationCommands.cs on the server). */
export const dictationCommands: [string, string][] = [
  ['new paragraph or new line', 'a new paragraph'],
  ['full stop', '.'], ['comma', ','], ['question mark', '?'], ['exclamation mark', '!'],
  ['colon', ':'], ['semicolon', ';'], ['open bracket … close bracket', '( )'], ['open quote … close quote', '" "'],
];
/** Afrikaans equivalents. */
export const dictationCommandsAf = 'nuwe paragraaf, nuwe reël, komma, vraagteken, uitroepteken, dubbelpunt, kommapunt';
