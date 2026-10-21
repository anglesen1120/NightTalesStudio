// Review F-3d: indexedDB outside core/prefs.
export const open = (): IDBOpenDBRequest => indexedDB.open('db');
