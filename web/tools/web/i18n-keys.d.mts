// Types for `i18n-keys.mjs` so the Vitest spec under `src/` can import the one shared implementation.
export declare const LANGS: readonly ['vi', 'en'];
export declare const CORE_SCOPE: 'core';
export interface HandedOverKey {
  readonly pattern: string;
  readonly scope: string;
}
export declare const HANDED_OVER_KEYS: readonly HandedOverKey[];
export interface ScopeKeys {
  readonly vi: readonly string[];
  readonly en: readonly string[];
}
export interface UsedKey {
  readonly scope: string;
  readonly key: string;
  readonly file?: string;
}
export declare function flattenKeys(obj: unknown): string[];
export declare function nonStringLeaves(obj: unknown): string[];
export declare function diffKeySets(a: readonly string[], b: readonly string[]): { onlyA: string[]; onlyB: string[] };
export declare function findDuplicateLeaves(text: string): string[];
export declare function normalizePattern(pattern: string): string;
export declare function matchesPattern(key: string, pattern: string): boolean;
export declare function scopeAlias(scope: string): string;
export declare function runtimeKey(scope: string, fileKey: string): string;
export declare function checkScopeFiles(
  scope: string,
  files: { vi: string | null; en: string | null },
): { problems: string[]; keys: { vi: string[]; en: string[] } };
export declare function checkHandedOverKeys(scopes: Readonly<Record<string, ScopeKeys>>): string[];
export declare function checkUsedKeys(used: readonly UsedKey[], scopes: Readonly<Record<string, ScopeKeys>>): string[];
