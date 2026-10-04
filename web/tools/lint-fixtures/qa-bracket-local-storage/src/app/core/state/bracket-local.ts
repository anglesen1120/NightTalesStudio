// QA defect: window['localStorage'] escapes the Identifier selector (storage only in core/prefs, D2/D6).
export const read = (): string | null => window['localStorage'].getItem('k');
