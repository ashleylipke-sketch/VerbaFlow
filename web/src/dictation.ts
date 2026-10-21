/** The spoken commands a dictation understands (kept in step with DictationCommands.cs on the server). */
export const dictationCommands: [string, string][] = [
  ['new paragraph or new line', 'a new paragraph'],
  ['bullet point (or insert bullet)', 'a bulleted point'], ['numbered point', 'the next numbered point (1, 2, 3 counted for you)'], ['end list', 'back to ordinary paragraphs'],
  ['full stop', '.'], ['comma', ','], ['question mark', '?'], ['exclamation mark', '!'],
  ['colon', ':'], ['semicolon', ';'], ['open bracket … close bracket', '( )'], ['open quote … close quote', '" "'],
  ['scratch that', 'takes away what you said since the last punctuation mark'],
  ['replace Tuesday with Thursday (or correct Tuesday to Thursday)', 'changes the last "Tuesday" you said; pause after the new words'],
];
/** Afrikaans equivalents. */
export const dictationCommandsAf = 'nuwe paragraaf, nuwe reël, komma, vraagteken, uitroepteken, dubbelpunt, kommapunt';
