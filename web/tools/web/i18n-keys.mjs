// Pure key helpers shared by `tools/web/check-i18n.mjs` (the `npm run i18n:check` CLI) and
// `src/app/core/i18n/i18n-keys.spec.ts` — one implementation (10.2.5-T1, README D10). No dependencies, no I/O.

/**
 * Files that make a scope: both languages are mandatory (README D10: `vi` default + `en`).
 * @type {readonly ['vi', 'en']}
 */
export const LANGS = Object.freeze(['vi', 'en']);

/** The scope whose JSON is Transloco's root translation: its keys are used as written (no scope prefix). */
export const CORE_SCOPE = 'core';

/**
 * Keys other plans hand over and that must be used verbatim (README D10). `scope` is the scope directory that owns
 * the pattern; a pattern is only checked while that directory exists. `*` matches exactly one segment.
 * @type {readonly { pattern: string; scope: string }[]}
 */
export const HANDED_OVER_KEYS = Object.freeze([
  { pattern: 'series.style.trueCrimeNotice', scope: 'series' },
  { pattern: 'series.style.*.label', scope: 'series' },
  { pattern: 'series.style.*.hint', scope: 'series' },
  { pattern: 'voices.tag.*', scope: 'series' },
  { pattern: 'quality.*', scope: 'quality' },
  { pattern: 'render.*', scope: 'render' },
]);

function isPlainObject(value) {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
}

function collectLeaves(value, prefix, out) {
  if (isPlainObject(value)) {
    for (const [key, child] of Object.entries(value)) {
      collectLeaves(child, prefix === '' ? key : `${prefix}.${key}`, out);
    }
  } else if (prefix !== '') {
    out.push({ path: prefix, value });
  }
}

/**
 * Dot paths of every leaf of a translation object, sorted. Arrays and primitives are leaves; an empty object has none.
 * @param {unknown} obj
 * @returns {string[]}
 */
export function flattenKeys(obj) {
  const leaves = [];
  collectLeaves(obj, '', leaves);
  return leaves.map((leaf) => leaf.path).sort();
}

/**
 * Dot paths of the leaves that are not strings (null, number, boolean, array), sorted.
 * @param {unknown} obj
 * @returns {string[]}
 */
export function nonStringLeaves(obj) {
  const leaves = [];
  collectLeaves(obj, '', leaves);
  return leaves
    .filter((leaf) => typeof leaf.value !== 'string')
    .map((leaf) => leaf.path)
    .sort();
}

/**
 * @param {readonly string[]} a
 * @param {readonly string[]} b
 * @returns {{ onlyA: string[]; onlyB: string[] }}
 */
export function diffKeySets(a, b) {
  const setA = new Set(a);
  const setB = new Set(b);
  return {
    onlyA: [...setA].filter((key) => !setB.has(key)).sort(),
    onlyB: [...setB].filter((key) => !setA.has(key)).sort(),
  };
}

function readJsonString(text, start) {
  let i = start + 1;
  let value = '';
  while (i < text.length) {
    const ch = text[i];
    if (ch === '\\') {
      value += text.slice(i, i + 2);
      i += 2;
    } else if (ch === '"') {
      return { value, end: i + 1 };
    } else {
      value += ch;
      i += 1;
    }
  }
  return { value, end: i };
}

/**
 * Raw-text scan of one JSON file for object keys that appear twice in the same object (JSON.parse keeps the last
 * one and would silently drop a translation). Returns the dot paths of the repeated keys, sorted.
 * @param {string} text
 * @returns {string[]}
 */
export function findDuplicateLeaves(text) {
  const duplicates = new Set();
  const stack = [];
  let i = 0;
  while (i < text.length) {
    const ch = text[i];
    const top = stack[stack.length - 1];
    if (ch === '"') {
      const { value, end } = readJsonString(text, i);
      i = end;
      if (top && top.type === 'object' && top.expectKey) {
        const path = [...top.path, value].join('.');
        if (top.keys.has(value)) duplicates.add(path);
        top.keys.add(value);
        top.pendingKey = value;
        top.expectKey = false;
      }
      continue;
    }
    if (ch === '{' || ch === '[') {
      const path = !top ? [] : top.type === 'object' ? [...top.path, top.pendingKey ?? ''] : [...top.path, `[${top.index}]`];
      stack.push(
        ch === '{'
          ? { type: 'object', keys: new Set(), path, pendingKey: null, expectKey: true }
          : { type: 'array', path, index: 0 },
      );
    } else if (ch === '}' || ch === ']') {
      stack.pop();
    } else if (ch === ',' && top) {
      if (top.type === 'object') top.expectKey = true;
      else top.index += 1;
    }
    i += 1;
  }
  return [...duplicates].sort();
}

/**
 * `<name>` / `<tag>` placeholders are wildcards too.
 * @param {string} pattern
 * @returns {string}
 */
export function normalizePattern(pattern) {
  return pattern.replace(/<[^<>.]*>/g, '*');
}

/**
 * `*` (or a `<name>` placeholder) matches exactly one non-empty segment; every other segment must be equal.
 * @param {string} key
 * @param {string} pattern
 * @returns {boolean}
 */
export function matchesPattern(key, pattern) {
  const keySegments = key.split('.');
  const patternSegments = normalizePattern(pattern).split('.');
  if (keySegments.length !== patternSegments.length) return false;
  return patternSegments.every((segment, index) => {
    const actual = keySegments[index] ?? '';
    return segment === '*' ? actual !== '' : segment === actual;
  });
}

/**
 * The key prefix Transloco gives a scope (`scopes.keepCasing: false` camel-cases it): `my-scope` → `myScope`.
 * @param {string} scope
 * @returns {string}
 */
export function scopeAlias(scope) {
  return scope.replace(/[-_]+([a-z0-9])/g, (_match, ch) => ch.toUpperCase());
}

/**
 * The key code passes to Transloco for a key written in a scope's file: the `core` file is the root translation
 * (used as written), every other scope's keys are prefixed with the scope alias.
 * @param {string} scope
 * @param {string} fileKey
 * @returns {string}
 */
export function runtimeKey(scope, fileKey) {
  return scope === CORE_SCOPE ? fileKey : `${scopeAlias(scope)}.${fileKey}`;
}

/**
 * Parity and hygiene of one scope from the raw text of its two files (`null` = file absent).
 * @param {string} scope
 * @param {{ vi: string | null; en: string | null }} files
 * @returns {{ problems: string[]; keys: { vi: string[]; en: string[] } }}
 */
export function checkScopeFiles(scope, files) {
  const problems = [];
  const keys = { vi: [], en: [] };
  const parsed = {};
  for (const lang of LANGS) {
    const text = files[lang];
    if (text === null) {
      problems.push(`${scope}: missing language file (${lang}.json)`);
      continue;
    }
    let value;
    try {
      value = JSON.parse(text);
    } catch (error) {
      problems.push(`${scope}/${lang}: invalid JSON (${error instanceof Error ? error.message : String(error)})`);
      continue;
    }
    if (!isPlainObject(value)) {
      problems.push(`${scope}/${lang}: root must be a JSON object`);
      continue;
    }
    for (const duplicate of findDuplicateLeaves(text)) {
      problems.push(`${scope}: duplicate leaf ${duplicate} (${lang}.json)`);
    }
    for (const leaf of nonStringLeaves(value)) {
      problems.push(`${scope}/${lang}: non-string leaf ${leaf}`);
    }
    parsed[lang] = value;
    keys[lang] = flattenKeys(value);
  }
  if (parsed.vi && parsed.en) {
    const { onlyA, onlyB } = diffKeySets(keys.vi, keys.en);
    for (const key of onlyB) problems.push(`${scope}/vi: missing key ${key}`);
    for (const key of onlyA) problems.push(`${scope}/en: missing key ${key}`);
  }
  return { problems, keys };
}

/**
 * Handed-over patterns owned by a scope that exists must match at least one of its keys, as written in the file or
 * as code uses it (scope alias prefix).
 * @param {Readonly<Record<string, { vi: readonly string[]; en: readonly string[] }>>} scopes flattened keys per existing scope
 * @returns {string[]}
 */
export function checkHandedOverKeys(scopes) {
  const problems = [];
  for (const { pattern, scope } of HANDED_OVER_KEYS) {
    const present = scopes[scope];
    if (!present) continue;
    const fileKeys = [...new Set([...present.vi, ...present.en])];
    const candidates = fileKeys.flatMap((key) => [key, runtimeKey(scope, key)]);
    if (!candidates.some((candidate) => matchesPattern(candidate, pattern))) {
      problems.push(`${scope}: handed-over key ${pattern} not found`);
    }
  }
  return problems;
}

/**
 * Used keys with no entry in their scope's files (either language counts; parity is a separate check).
 * @param {readonly { scope: string; key: string; file?: string }[]} used `key` is the key as written in the scope's file
 * @param {Readonly<Record<string, { vi: readonly string[]; en: readonly string[] }>>} scopes flattened keys per existing scope
 * @returns {string[]}
 */
export function checkUsedKeys(used, scopes) {
  const problems = [];
  for (const { scope, key, file } of used) {
    const present = scopes[scope];
    if (present && (present.vi.includes(key) || present.en.includes(key))) continue;
    const where = file ? ` (${file})` : '';
    problems.push(`used but undefined: ${runtimeKey(scope, key)}${where}`);
  }
  return problems;
}
