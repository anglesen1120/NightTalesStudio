#!/usr/bin/env node
// Adopts a new OpenAPI artifact (task 10.1.4-T1, README D5):
//   npm run api:gen:local -- <path-to-openapi.json> [--source-commit=<40-hex sha>]
//
// Generates src/app/shared/api/** from <path> into a temp directory first; only on success replaces the api tree,
// then replaces the vendored web/openapi.json and rewrites web/tools/web/openapi.lock.json (source, info.version,
// SHA-256, sourceCommit). A failed generation leaves all three untouched. Review the resulting diff in the same PR.
// sourceCommit is the backend commit the spec was built from: `git rev-parse HEAD` of the repository that
// contains <path>; pass --source-commit when <path> is a downloaded artifact outside any git checkout.
// Node built-ins only.
import { spawnSync } from 'node:child_process';
import { createHash } from 'node:crypto';
import { copyFileSync, cpSync, existsSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const webRoot = resolve(dirname(fileURLToPath(import.meta.url)), '..', '..');
const LOCK_SOURCE = 'artifact openapi (09.1.5-T1)';

const args = process.argv.slice(2);
const SOURCE_COMMIT_FLAG = '--source-commit=';
const flagArg = args.find((arg) => arg.startsWith(SOURCE_COMMIT_FLAG));
const input = args.find((arg) => !arg.startsWith('--'));
if (!input) {
  console.error('usage: npm run api:gen:local -- <path-to-openapi.json> [--source-commit=<40-hex sha>]');
  process.exit(2);
}
const inputPath = resolve(input);
if (!existsSync(inputPath)) {
  console.error(`api:gen:local: spec not found: ${inputPath}`);
  process.exit(2);
}
const bytes = readFileSync(inputPath);
let document;
try {
  document = JSON.parse(bytes.toString('utf8'));
} catch (error) {
  console.error(`api:gen:local: ${inputPath} is not valid JSON (${error.message})`);
  process.exit(2);
}
if (typeof document?.info?.version !== 'string' || document.info.version === '') {
  console.error(`api:gen:local: ${inputPath} has no info.version`);
  process.exit(2);
}

/** The backend commit the spec was built from: the explicit flag, else HEAD of the git checkout containing the spec. */
function resolveSourceCommit() {
  if (flagArg) {
    const value = flagArg.slice(SOURCE_COMMIT_FLAG.length);
    if (!/^[0-9a-f]{40}$/.test(value)) {
      console.error('api:gen:local: --source-commit must be a full 40-character lowercase hex sha');
      process.exit(2);
    }
    return value;
  }
  const head = spawnSync('git', ['rev-parse', 'HEAD'], { cwd: dirname(inputPath), encoding: 'utf8' });
  const sha = head.status === 0 ? head.stdout.trim() : '';
  if (!/^[0-9a-f]{40}$/.test(sha)) {
    console.error(`api:gen:local: ${inputPath} is not inside a git checkout; pass --source-commit=<sha of the backend commit it was built from>`);
    process.exit(2);
  }
  return sha;
}
const sourceCommit = resolveSourceCommit();

const specPath = join(webRoot, 'openapi.json');
const apiDir = join(webRoot, 'src', 'app', 'shared', 'api');
const lockPath = join(webRoot, 'tools', 'web', 'openapi.lock.json');

// Generate into a temp directory from the input spec; only a successful generation touches the repo.
const tempDir = mkdtempSync(join(tmpdir(), 'nts-api-gen-local-'));
let exitCode = 0;
try {
  const tempOut = join(tempDir, 'api');
  const tempConfig = {
    ...JSON.parse(readFileSync(join(webRoot, 'api-gen.config.json'), 'utf8')),
    input: inputPath,
    output: tempOut,
  };
  const tempConfigPath = join(tempDir, 'api-gen.config.json');
  writeFileSync(tempConfigPath, `${JSON.stringify(tempConfig, null, 2)}\n`);

  const generated = spawnSync(
    process.execPath,
    [join(webRoot, 'node_modules', 'ng-openapi-gen', 'lib', 'index.js'), '--config', tempConfigPath],
    { cwd: webRoot, stdio: 'inherit' },
  );
  if (generated.status !== 0) {
    exitCode = generated.status ?? 1;
  } else {
    rmSync(apiDir, { recursive: true, force: true });
    cpSync(tempOut, apiDir, { recursive: true });
    if (inputPath !== specPath) {
      copyFileSync(inputPath, specPath);
    }
    const lock = {
      source: LOCK_SOURCE,
      specVersion: document.info.version,
      sha256: createHash('sha256').update(bytes).digest('hex'),
      sourceCommit,
    };
    writeFileSync(lockPath, `${JSON.stringify(lock, null, 2)}\n`);
  }
} finally {
  rmSync(tempDir, { recursive: true, force: true });
}
process.exit(exitCode);
