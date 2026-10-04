#!/usr/bin/env node
// `npm run i18n:check` (10.2.5-T1, README D10/D15): the single CI entry point for translation hygiene.
//   1. Parity   — every scope directory under src/assets/i18n has vi.json + en.json with equal key sets,
//                 no duplicate keys, no non-string leaves.
//   2. Handed-over keys — patterns other plans own (HANDED_OVER_KEYS) exist once their scope directory exists.
//   3. Used keys — every key the code uses (`@jsverse/transloco-keys-manager extract`, marker `t`) is defined in
//                 its scope's files.
// Exit 1 with one line per violation; exit 0 printing `i18n ok: N scopes, M keys, K used keys`.
//
// Usage: node tools/web/check-i18n.mjs [--root <web dir>]

import { spawnSync } from 'node:child_process';
import { existsSync, mkdtempSync, readFileSync, readdirSync, rmSync, statSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { dirname, join, relative, resolve, sep } from 'node:path';
import { fileURLToPath } from 'node:url';
import {
  CORE_SCOPE,
  LANGS,
  checkHandedOverKeys,
  checkScopeFiles,
  checkUsedKeys,
  flattenKeys,
  runtimeKey,
  scopeAlias,
} from './i18n-keys.mjs';

const TOOL_DIR = dirname(fileURLToPath(import.meta.url));
const DEFAULT_ROOT = resolve(TOOL_DIR, '..', '..');
const KEYS_MANAGER_BIN = join(DEFAULT_ROOT, 'node_modules', '.bin', 'transloco-keys-manager');

const I18N_DIR = join('src', 'assets', 'i18n');
const SOURCE_DIR = join('src', 'app');

function readIfExists(path) {
  return existsSync(path) ? readFileSync(path, 'utf8') : null;
}

function walk(dir, accept) {
  return readdirSync(dir, { withFileTypes: true }).flatMap((entry) => {
    const path = join(dir, entry.name);
    if (entry.isDirectory()) return walk(path, accept);
    return accept(path) ? [path] : [];
  });
}

/** Parity + hygiene of every scope directory; returns the flattened keys of the scopes that could be read. */
function checkScopes(root) {
  const i18nRoot = join(root, I18N_DIR);
  const problems = [];
  const scopes = {};
  const entries = existsSync(i18nRoot) ? readdirSync(i18nRoot, { withFileTypes: true }) : [];
  for (const entry of entries) {
    if (!entry.isDirectory()) {
      problems.push(`${relative(root, join(i18nRoot, entry.name))}: unexpected file outside a scope directory`);
      continue;
    }
    const scope = entry.name;
    const dir = join(i18nRoot, scope);
    for (const name of readdirSync(dir)) {
      if (!LANGS.some((lang) => name === `${lang}.json`)) {
        problems.push(`${scope}: unexpected file ${name} (only ${LANGS.map((l) => `${l}.json`).join(' and ')})`);
      }
    }
    const result = checkScopeFiles(scope, {
      vi: readIfExists(join(dir, 'vi.json')),
      en: readIfExists(join(dir, 'en.json')),
    });
    problems.push(...result.problems);
    scopes[scope] = result.keys;
  }
  return { problems, scopes };
}

/**
 * Runs the pinned keys-manager `extract` into a scratch directory and returns the used keys per scope. The extractor
 * names a scope's output directory by its Transloco alias (`my-scope` → `myScope`); `scopeDirs` maps it back.
 */
function extractUsedKeys(root, keysManagerBin, scopeDirs) {
  const dirByAlias = new Map(scopeDirs.map((dir) => [scopeAlias(dir), dir]));
  const scratch = mkdtempSync(join(tmpdir(), 'nts-i18n-used-'));
  try {
    const run = spawnSync(
      keysManagerBin,
      ['extract', '--input', SOURCE_DIR, '--output', scratch, '--langs', 'en', '--marker', 't', '--replace'],
      { cwd: root, encoding: 'utf8', timeout: 120_000 },
    );
    if (run.error || run.status !== 0) {
      const detail = run.error ? run.error.message : `${run.stdout ?? ''}${run.stderr ?? ''}`.trim().split('\n').slice(-5).join(' | ');
      return { error: `used keys: keys-manager extract failed (exit ${run.status ?? 'none'}): ${detail}`, used: [] };
    }
    const used = [];
    for (const file of walk(scratch, (path) => path.endsWith(`${sep}en.json`))) {
      const relativeDir = relative(scratch, dirname(file));
      const alias = relativeDir === '' ? CORE_SCOPE : relativeDir.split(sep).join('/');
      const scope = dirByAlias.get(alias) ?? alias;
      for (const key of flattenKeys(JSON.parse(readFileSync(file, 'utf8')))) used.push({ scope, key });
    }
    return { error: null, used };
  } finally {
    rmSync(scratch, { recursive: true, force: true });
  }
}

/** First source file (non-spec preferred) that mentions the key, for the `used but undefined` line. */
function attributeFiles(root, used) {
  const sourceRoot = join(root, SOURCE_DIR);
  if (!existsSync(sourceRoot) || !statSync(sourceRoot).isDirectory()) return used;
  const files = walk(sourceRoot, (path) => /\.(ts|html)$/.test(path))
    .map((path) => ({ path: relative(root, path).split(sep).join('/'), text: readFileSync(path, 'utf8') }))
    .sort((a, b) => Number(/\.spec\.ts$/.test(a.path)) - Number(/\.spec\.ts$/.test(b.path)) || a.path.localeCompare(b.path));
  return used.map((entry) => {
    const full = runtimeKey(entry.scope, entry.key);
    const hit = files.find((file) => file.text.includes(full)) ?? files.find((file) => file.text.includes(entry.key));
    return hit ? { ...entry, file: hit.path } : entry;
  });
}

/**
 * @param {{ root?: string; keysManagerBin?: string }} [options]
 * @returns {{ problems: string[]; summary: string }}
 */
export function runCheck({ root = DEFAULT_ROOT, keysManagerBin = KEYS_MANAGER_BIN } = {}) {
  const { problems, scopes } = checkScopes(root);
  problems.push(...checkHandedOverKeys(scopes));

  const extraction = extractUsedKeys(root, keysManagerBin, Object.keys(scopes));
  if (extraction.error) problems.push(extraction.error);
  const used = attributeFiles(root, extraction.used);
  problems.push(...checkUsedKeys(used, scopes));

  const scopeNames = Object.keys(scopes);
  const keyCount = scopeNames.reduce((sum, scope) => sum + (scopes[scope]?.vi.length ?? 0), 0);
  return { problems, summary: `i18n ok: ${scopeNames.length} scopes, ${keyCount} keys, ${used.length} used keys` };
}

function main() {
  const rootFlag = process.argv.indexOf('--root');
  const root = rootFlag > -1 ? resolve(process.argv[rootFlag + 1] ?? '') : DEFAULT_ROOT;
  const { problems, summary } = runCheck({ root });
  if (problems.length > 0) {
    for (const line of problems) console.error(line);
    console.error(`i18n FAIL — ${problems.length} problem(s)`);
    process.exit(1);
  }
  console.log(summary);
}

if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) main();
