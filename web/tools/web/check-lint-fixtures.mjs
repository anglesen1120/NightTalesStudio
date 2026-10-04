#!/usr/bin/env node
// Negative-fixture matrix for the lint rules (task 10.1.2-T1).
//
// For every entry of tools/lint-fixtures/expected.json:
//   linter "eslint"    -> `eslint --no-ignore --format json <files>` (the fixtures sit in the config's global
//                         `ignores`, so --no-ignore is required); the reported ruleIds must equal `rules`.
//   linter "forbidden" -> tools/web/lint-forbidden-patterns.mjs <file>; the reported pattern names must equal `rules`.
// `rules: []` marks a positive control that must stay clean. Comparison is an exact multiset match.
// Prints a PASS/FAIL table and exits 1 on any mismatch, a missing fixture file or an unlisted fixture file.
//
// Adding a rule: add a minimal violating file under tools/lint-fixtures/<case>/src/app/<real path>/ and one
// expected.json entry. The fixture must live at the path the rule applies to (the config mirrors every glob
// under tools/lint-fixtures/*/src/).
import { spawnSync } from 'node:child_process';
import { existsSync, readdirSync, readFileSync } from 'node:fs';
import { dirname, join, relative, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const webRoot = resolve(dirname(fileURLToPath(import.meta.url)), '..', '..');
const fixtureRoot = join(webRoot, 'tools', 'lint-fixtures');
const eslintBin = join(webRoot, 'node_modules', 'eslint', 'bin', 'eslint.js');
const forbiddenScript = join(webRoot, 'tools', 'web', 'lint-forbidden-patterns.mjs');
const FIXTURE_SUFFIXES = ['.ts', '.html'];

const { fixtures } = JSON.parse(readFileSync(join(fixtureRoot, 'expected.json'), 'utf8'));
const problems = [];
const rows = [];

function* listFiles(dir) {
  for (const entry of readdirSync(dir, { withFileTypes: true })) {
    const path = join(dir, entry.name);
    if (entry.isDirectory()) {
      yield* listFiles(path);
    } else if (FIXTURE_SUFFIXES.some((suffix) => entry.name.endsWith(suffix))) {
      yield path;
    }
  }
}

// Every fixture file on disk must be declared, and every declared file must exist.
const declared = new Set(fixtures.map((fixture) => fixture.file));
for (const path of listFiles(fixtureRoot)) {
  const key = relative(fixtureRoot, path);
  if (!declared.has(key)) {
    problems.push(`fixture file not listed in expected.json: ${key}`);
  }
}
const present = fixtures.filter((fixture) => {
  const exists = existsSync(join(fixtureRoot, fixture.file));
  if (!exists) {
    problems.push(`expected.json lists a missing file: ${fixture.file}`);
  }
  return exists;
});

// --- eslint: one run over the explicit file list ---------------------------------------------------------------
const eslintFixtures = present.filter((fixture) => fixture.linter === 'eslint');
const actualByFile = new Map();
if (eslintFixtures.length > 0) {
  const run = spawnSync(
    process.execPath,
    [eslintBin, '--no-ignore', '--format', 'json', ...eslintFixtures.map((fixture) => join(fixtureRoot, fixture.file))],
    { cwd: webRoot, encoding: 'utf8', maxBuffer: 64 * 1024 * 1024 },
  );
  let results;
  try {
    results = JSON.parse(run.stdout);
  } catch {
    console.error(run.stderr || run.stdout);
    console.error('lint:fixtures FAIL — eslint did not produce JSON output (config error?)');
    process.exit(1);
  }
  for (const result of results) {
    const ruleIds = result.messages.map((message) => message.ruleId ?? `<fatal: ${message.message}>`);
    actualByFile.set(relative(fixtureRoot, result.filePath), ruleIds);
  }
}

// --- forbidden-pattern scan: one run per template fixture ------------------------------------------------------
for (const fixture of present.filter((entry) => entry.linter === 'forbidden')) {
  const run = spawnSync(process.execPath, [forbiddenScript, join(fixtureRoot, fixture.file)], {
    cwd: webRoot,
    encoding: 'utf8',
  });
  const names = run.stderr
    .split('\n')
    .filter((line) => /^\S+:\d+: /.test(line))
    .map((line) => line.replace(/^\S+:\d+: /, ''));
  actualByFile.set(fixture.file, names);
}

for (const fixture of present) {
  const actual = [...(actualByFile.get(fixture.file) ?? [])].sort();
  const expected = [...fixture.rules].sort();
  const pass = actual.length === expected.length && actual.every((rule, index) => rule === expected[index]);
  rows.push({ pass, fixture, expected, actual });
}

const label = (rules) => (rules.length === 0 ? '(clean)' : rules.join(', '));
const width = Math.max(...rows.map((row) => row.fixture.file.length));
for (const { pass, fixture, expected, actual } of rows) {
  const line = `${pass ? 'PASS' : 'FAIL'}  ${fixture.file.padEnd(width)}  ${label(expected)}`;
  console.log(pass ? line : `${line}\n      actual: ${label(actual)}`);
}
for (const problem of problems) {
  console.log(`FAIL  ${problem}`);
}

const failed = rows.filter((row) => !row.pass).length + problems.length;
const passed = rows.length - rows.filter((row) => !row.pass).length;
console.log(`lint:fixtures ${failed === 0 ? 'PASS' : 'FAIL'} — ${passed}/${rows.length} fixtures matched` +
  (problems.length > 0 ? `, ${problems.length} listing problem(s)` : ''));
process.exit(failed === 0 ? 0 : 1);
