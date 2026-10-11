export type DocType = 'letter' | 'document' | 'email';

export const DOC_TYPES: { value: DocType; label: string; hint: string }[] = [
  { value: 'letter', label: 'Letter', hint: 'Dated, with your reference' },
  { value: 'document', label: 'Word document', hint: 'Titled, in paragraphs' },
  { value: 'email', label: 'Email', hint: 'With a subject line' },
];

export const docLabel = (t: string | null | undefined) => DOC_TYPES.find(d => d.value === t)?.label ?? 'Text';

/** Asked before a dictation starts, so the text can be shown and exported in that shape. */
export default function DocumentTypePicker({ value, onChange }: { value: DocType | ''; onChange: (v: DocType) => void }) {
  return (
    <fieldset className="doctypes">
      <legend>What are you dictating?</legend>
      <div className="doctype-row">
        {DOC_TYPES.map(d => (
          <label key={d.value} className={'doctype' + (value === d.value ? ' on' : '')}>
            <input type="radio" name="doctype" value={d.value} checked={value === d.value} onChange={() => onChange(d.value)} />
            <span className="doctype-name">{d.label}</span><span className="note">{d.hint}</span>
          </label>))}
      </div>
    </fieldset>
  );
}
