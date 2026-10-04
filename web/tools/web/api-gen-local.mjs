#!/usr/bin/env node
// Adopts a new OpenAPI artifact (task 10.1.4-T1, README D5):
//
//   npm run api:gen:local -- <path-to-openapi.json>
//
// Replaces the vendored web/openapi.json with <path>, rewrites web/tools/web/openapi.lock.json (source,
// info.version, SHA-256) and regenerates src/app/shared/api/** from it. Review the resulting diff in the same PR.
// Node built-ins only.
import { spawnSync } from 'node:child_process';
import { createHash } from 'node:crypto';
import { copyFileSync, existsSync, readFileSync, writeFileSync } from 'node:fs';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const webRoot = resolve(dirname(fileURLToPath(import.meta.url)), '..', '..');
const LOCK_SOURCE = 'artifact openapi (09.1.5-T1)';

const input = process.argv[2];
if (!input) {
  console.error('usage: npm run api:gen:local -- <path-to-openapi.json>');
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

const specPath = join(webRoot, 'openapi.json');
if (inputPath !== specPath) {
  copyFileSync(inputPath, specPath);
}
const lock = { source: LOCK_SOURCE, specVersion: document.info.version, sha256: createHash('sha256').update(bytes).digest('hex') };
writeFileSync(join(webRoot, 'tools', 'web', 'openapi.lock.json'), `${JSON.stringify(lock, null, 2)}\n`);

const generated = spawnSync(
  process.execPath,
  [join(webRoot, 'node_modules', 'ng-openapi-gen', 'lib', 'index.js'), '--config', 'api-gen.config.json'],
  { cwd: webRoot, stdio: 'inherit' },
);
process.exit(generated.status ?? 1);
