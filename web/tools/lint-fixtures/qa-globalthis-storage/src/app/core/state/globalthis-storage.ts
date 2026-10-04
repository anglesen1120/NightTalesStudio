// QA guard: globalThis.localStorage (property access) is reported outside core/prefs.
export const read = (): string | null => globalThis.localStorage.getItem('k');
