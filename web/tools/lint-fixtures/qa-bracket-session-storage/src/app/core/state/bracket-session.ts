// QA defect: globalThis['sessionStorage'] escapes the Identifier selector (storage only in core/prefs, D2/D6).
export const read = (): string | null => globalThis['sessionStorage'].getItem('k');
