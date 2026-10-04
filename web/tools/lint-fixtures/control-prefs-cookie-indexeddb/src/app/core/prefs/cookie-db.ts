// Control: core/prefs keeps its storage exemption for document.cookie and indexedDB.
export const read = (): string => document.cookie;
export const open = (): IDBOpenDBRequest => indexedDB.open('prefs');
