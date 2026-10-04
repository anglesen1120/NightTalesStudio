#!/usr/bin/env node
// Drift + guard check for the generated API client (task 10.1.4-T1, README D5/D3, 10 CR-43).
//
//   npm run api:gen:check   (alias: npm run api:check)
//
// 1. Pre-flight: `openapi.json` must exist and parse, and every key of `api-gen.config.json` must be a
//    generator option of the pinned ng-openapi-gen schema (an unsupported option fails instead of being ignored).
// 2. Regenerates the client into a temp directory (os.tmpdir()); the working tree is never touched, so a
//    failure cannot leave a half-regenerated tree behind.
// 3. Byte-compares every regenerated file with the committed tree and reports added / removed / changed files
//    with the first differing line.
// 4. Absolute-URL guard: no generated file may carry an http(s):// literal (the SPA is same-origin, D3). Comments
//    are ignored: the generator's own request-builder.ts links a GitHub issue in a comment.
// 5. Shape guard: the barrel (index.ts) must export the ProblemDetails model that AppErrorMapper consumes.
//
// Guards 4 and 5 run on the regenerated tree and on the committed tree. Exit 0 = clean, 1 = any finding.
// Node built-ins only.
import { spawnSync } from 'node:child_process';
import { existsSync, mkdtempSync, readFileSync, readdirSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { dirname, join, relative, resolve, sep } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';

const scriptWebRoot = resolve(dirname(fileURLToPath(import.meta.url)), '..', '..');
const GENERATOR_DIR = join(scriptWebRoot, 'node_modules', 'ng-openapi-gen');

/** Relative posix paths of every file below `dir`, sorted. A missing directory is an empty tree. */
export function listFiles(dir) {
  const out = [];
  const walk = (current) => {
    if (!existsSync(current)) {
      return;
    }
    for (const entry of readdirSync(current, { withFileTypes: true })) {
      const full = join(current, entry.name);
      if (entry.isDirectory()) {
        walk(full);
      } else if (entry.isFile()) {
        out.push(relative(dir, full).split(sep).join('/'));
      }
    }
  };
  walk(dir);
  return out.sort();
}

function firstDifference(expected, actual) {
  const expectedLines = expected.toString('utf8').split('\n');
  const actualLines = actual.toString('utf8').split('\n');
  const length = Math.max(expectedLines.length, actualLines.length);
  for (let index = 0; index < length; index += 1) {
    if (expectedLines[index] !== actualLines[index]) {
      return { line: index + 1, expected: expectedLines[index], actual: actualLines[index] };
    }
  }
  return { line: 0, expected: undefined, actual: undefined };
}

/**
 * Compares the freshly generated tree (`expectedDir`) with the committed one (`actualDir`).
 * Returns { total, added, removed, changed }: `added` = generated but not committed, `removed` = committed but not
 * generated, `changed` = { file, line, expected, actual }.
 */
export function compareTrees(expectedDir, actualDir) {
  const expectedFiles = listFiles(expectedDir);
  const actualFiles = new Set(listFiles(actualDir));
  const expectedSet = new Set(expectedFiles);
  const added = expectedFiles.filter((file) => !actualFiles.has(file));
  const removed = [...actualFiles].filter((file) => !expectedSet.has(file)).sort();
  const changed = [];
  for (const file of expectedFiles) {
    if (!actualFiles.has(file)) {
      continue;
    }
    const expected = readFileSync(join(expectedDir, file));
    const actual = readFileSync(join(actualDir, file));
    if (!expected.equals(actual)) {
      changed.push({ file, ...firstDifference(expected, actual) });
    }
  }
  return { total: expectedFiles.length, added, removed, changed };
}

/** Source with every comment removed (line structure kept); string and template literals are left intact. */
export function stripComments(source) {
  let out = '';
  let index = 0;
  const length = source.length;
  while (index < length) {
    const char = source[index];
    const next = source[index + 1];
    if (char === '/' && next === '/') {
      while (index < length && source[index] !== '\n') {
        index += 1;
      }
    } else if (char === '/' && next === '*') {
      index += 2;
      while (index < length && !(source[index] === '*' && source[index + 1] === '/')) {
        if (source[index] === '\n') {
          out += '\n';
        }
        index += 1;
      }
      index += 2;
    } else if (char === "'" || char === '"' || char === '`') {
      out += char;
      index += 1;
      while (index < length && source[index] !== char) {
        if (source[index] === '\\') {
          out += source[index];
          index += 1;
        }
        if (index < length) {
          out += source[index];
          index += 1;
        }
      }
      if (index < length) {
        out += source[index];
        index += 1;
      }
    } else {
      out += char;
      index += 1;
    }
  }
  return out;
}

const ABSOLUTE_URL = /https?:\/\//i;
const PROBLEM_DETAILS_EXPORT = /export\s+(?:type\s+)?\{[^}]*\bProblemDetails\b[^}]*\}/;

/** Guards over one generated tree. Returns [{ kind: 'absolute-url' | 'shape', file, line?, message }]. */
export function scanGuards(dir, label = 'generated') {
  const findings = [];
  for (const file of listFiles(dir)) {
    const lines = stripComments(readFileSync(join(dir, file), 'utf8')).split('\n');
    lines.forEach((text, index) => {
      if (ABSOLUTE_URL.test(text)) {
        findings.push({
          kind: 'absolute-url',
          file,
          line: index + 1,
          message: `${label} ${file}:${index + 1} contains an absolute http(s):// URL; the SPA is same-origin (D3), fix the spec's servers in 09.9.5-T1`,
        });
      }
    });
  }
  const indexPath = join(dir, 'index.ts');
  if (!existsSync(indexPath) || !PROBLEM_DETAILS_EXPORT.test(readFileSync(indexPath, 'utf8'))) {
    findings.push({
      kind: 'shape',
      file: 'index.ts',
      message: `${label} index.ts does not export the ProblemDetails model that AppErrorMapper (10.2.2-T1) consumes; the backend openapi.json lost it (09.9.5-T1)`,
    });
  }
  return findings;
}

/** Keys of the generator config that the pinned ng-openapi-gen schema does not know. */
export function unsupportedConfigKeys(config, schema) {
  const known = new Set(Object.keys(schema.properties ?? {}));
  return Object.keys(config).filter((key) => !known.has(key));
}

function parseJsonFile(path, what) {
  if (!existsSync(path)) {
    return { error: `${what} is missing: ${path}` };
  }
  try {
    return { value: JSON.parse(readFileSync(path, 'utf8')) };
  } catch (error) {
    return { error: `${what} is not valid JSON: ${path} (${error.message})` };
  }
}

function parseArgs(argv) {
  const options = { webRoot: scriptWebRoot };
  for (let index = 0; index < argv.length; index += 1) {
    if (argv[index] === '--web-root' && argv[index + 1]) {
      options.webRoot = resolve(argv[index + 1]);
      index += 1;
    }
  }
  return options;
}

/** Runs the whole check. Returns the process exit code; `log` / `logError` receive the output lines. */
export function main(argv = process.argv.slice(2), log = console.log, logError = console.error) {
  const { webRoot } = parseArgs(argv);
  const specPath = join(webRoot, 'openapi.json');
  const configPath = join(webRoot, 'api-gen.config.json');
  const committedDir = join(webRoot, 'src', 'app', 'shared', 'api');
  const failures = [];

  const spec = parseJsonFile(specPath, 'openapi.json');
  if (spec.error) {
    logError(`spec: ${spec.error}`);
    log('api:check failed: spec');
    return 1;
  }
  const config = parseJsonFile(configPath, 'api-gen.config.json');
  if (config.error) {
    logError(`config: ${config.error}`);
    log('api:check failed: config');
    return 1;
  }
  const schema = parseJsonFile(join(GENERATOR_DIR, 'ng-openapi-gen-schema.json'), 'ng-openapi-gen schema');
  if (schema.error) {
    logError(`config: ${schema.error} (run npm ci)`);
    log('api:check failed: config');
    return 1;
  }
  const unsupported = unsupportedConfigKeys(config.value, schema.value);
  if (unsupported.length > 0) {
    logError(`config: api-gen.config.json has options the pinned ng-openapi-gen does not support: ${unsupported.join(', ')}`);
    log('api:check failed: config');
    return 1;
  }

  const scratch = mkdtempSync(join(tmpdir(), 'nts-api-drift-'));
  try {
    const outDir = join(scratch, 'api');
    const result = spawnSync(
      process.execPath,
      [join(GENERATOR_DIR, 'lib', 'index.js'), '--config', configPath, '--input', specPath, '--output', outDir],
      { cwd: webRoot, encoding: 'utf8' },
    );
    if (result.status !== 0) {
      logError(`generator: ng-openapi-gen failed with exit code ${result.status}\n${result.stdout}${result.stderr}`);
      log('api:check failed: generator');
      return 1;
    }

    const diff = compareTrees(outDir, committedDir);
    const driftCount = diff.added.length + diff.removed.length + diff.changed.length;
    if (driftCount === 0) {
      log(`ok: ${diff.total} files`);
    } else {
      log(`drift: ${driftCount} files`);
      for (const file of diff.added) {
        logError(`  missing  ${file} (generated, not committed)`);
      }
      for (const file of diff.removed) {
        logError(`  stale    ${file} (committed, no longer generated)`);
      }
      for (const { file, line, expected, actual } of diff.changed) {
        logError(`  changed  ${file}:${line}\n    generated: ${expected ?? '(end of file)'}\n    committed: ${actual ?? '(end of file)'}`);
      }
      failures.push('drift');
    }

    const findings = [...scanGuards(outDir, 'regenerated'), ...scanGuards(committedDir, 'committed')];
    for (const finding of findings) {
      logError(`${finding.kind}: ${finding.message}`);
      if (!failures.includes(finding.kind)) {
        failures.push(finding.kind);
      }
    }
  } finally {
    rmSync(scratch, { recursive: true, force: true });
  }

  if (failures.length > 0) {
    log(`api:check failed: ${failures.join(', ')}`);
    return 1;
  }
  return 0;
}

if (process.argv[1] && import.meta.url === pathToFileURL(resolve(process.argv[1])).href) {
  process.exit(main());
}
