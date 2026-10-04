import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { cpSync, existsSync, mkdirSync, mkdtempSync, readdirSync, readFileSync, rmSync, symlinkSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { dirname, join } from 'node:path';
import { after, describe, it } from 'node:test';
import { fileURLToPath } from 'node:url';

const toolsDir = dirname(fileURLToPath(import.meta.url));
const webRoot = join(toolsDir, '..', '..');
const GENERATOR = join(webRoot, 'node_modules', 'ng-openapi-gen', 'lib', 'index.js');
const SOURCE_COMMIT = 'a'.repeat(40);

const scratch = mkdtempSync(join(tmpdir(), 'nts-api-gen-local-test-'));
after(() => rmSync(scratch, { recursive: true, force: true }));

const spec = (version) => ({
  openapi: '3.1.1',
  info: { title: 'fixture', version },
  paths: {
    '/api/v1/ping': {
      get: {
        tags: ['Ping'],
        operationId: 'ping',
        responses: {
          200: { description: 'ok', content: { 'application/json': { schema: { type: 'string' } } } },
          400: { description: 'bad', content: { 'application/problem+json': { schema: { $ref: '#/components/schemas/ProblemDetails' } } } },
        },
      },
    },
  },
  components: {
    schemas: {
      ProblemDetails: {
        type: 'object',
        required: ['code', 'traceId'],
        properties: { code: { type: 'string' }, traceId: { type: 'string' } },
      },
    },
  },
});

let counter = 0;
/** A scratch web root (the script locates its web root from its own path) holding a committed spec, lock and client for 1.0.0. */
function project() {
  counter += 1;
  const root = join(scratch, `project-${counter}`);
  mkdirSync(join(root, 'tools', 'web'), { recursive: true });
  cpSync(join(toolsDir, 'api-gen-local.mjs'), join(root, 'tools', 'web', 'api-gen-local.mjs'));
  cpSync(join(webRoot, 'api-gen.config.json'), join(root, 'api-gen.config.json'));
  symlinkSync(join(webRoot, 'node_modules'), join(root, 'node_modules'));
  writeFileSync(join(root, 'openapi.json'), JSON.stringify(spec('1.0.0')));
  writeFileSync(join(root, 'tools', 'web', 'openapi.lock.json'), '{"specVersion":"1.0.0"}\n');
  const generated = spawnSync(process.execPath, [GENERATOR, '--config', 'api-gen.config.json'], { cwd: root, encoding: 'utf8' });
  assert.equal(generated.status, 0, generated.stdout + generated.stderr);
  return root;
}

/** Relative path -> content of every file under `dir`, so a before/after comparison catches any change. */
function snapshot(dir) {
  const files = {};
  const walk = (current) => {
    for (const entry of readdirSync(current, { withFileTypes: true })) {
      const full = join(current, entry.name);
      if (entry.isDirectory()) walk(full);
      else files[full.slice(dir.length)] = readFileSync(full, 'utf8');
    }
  };
  walk(dir);
  return files;
}

const targets = (root) => ({
  spec: readFileSync(join(root, 'openapi.json'), 'utf8'),
  lock: readFileSync(join(root, 'tools', 'web', 'openapi.lock.json'), 'utf8'),
  api: snapshot(join(root, 'src', 'app', 'shared', 'api')),
});

function input(name, document) {
  counter += 1;
  const path = join(scratch, `${name}-${counter}.json`);
  writeFileSync(path, typeof document === 'string' ? document : JSON.stringify(document));
  return path;
}

const run = (root, specPath, ...extra) => {
  const result = spawnSync(process.execPath, [join(root, 'tools', 'web', 'api-gen-local.mjs'), specPath, `--source-commit=${SOURCE_COMMIT}`, ...extra], {
    encoding: 'utf8',
  });
  return { code: result.status, out: result.stdout, err: result.stderr };
};

describe('api-gen-local (real generator, tiny spec)', () => {
  it('leaves spec, lock and generated client untouched when generation fails', () => {
    const root = project();
    writeFileSync(join(root, 'src', 'app', 'shared', 'api', 'hand-written.ts'), 'export {};\n');
    const before = targets(root);
    const broken = spec('2.0.0');
    broken.paths['/api/v1/ping'].get.responses[400].content['application/problem+json'].schema = { $ref: '#/components/schemas/Missing' };

    const { code, out, err } = run(root, input('broken', broken));

    assert.notEqual(code, 0);
    assert.match(out + err, /Couldn't resolve reference #\/components\/schemas\/Missing/);
    assert.deepEqual(targets(root), before);
  });

  it('replaces the client, spec and lock after a successful generation', () => {
    const root = project();
    const next = spec('2.0.0');
    next.paths['/api/v1/ping'].get.operationId = 'pong';
    const nextPath = input('next', next);
    writeFileSync(join(root, 'src', 'app', 'shared', 'api', 'hand-written.ts'), 'export {};\n');

    const { code, out, err } = run(root, nextPath);

    assert.equal(code, 0, out + err);
    assert.equal(readFileSync(join(root, 'openapi.json'), 'utf8'), readFileSync(nextPath, 'utf8'));
    const lock = JSON.parse(readFileSync(join(root, 'tools', 'web', 'openapi.lock.json'), 'utf8'));
    assert.equal(lock.specVersion, '2.0.0');
    assert.equal(lock.sourceCommit, SOURCE_COMMIT);
    assert.match(lock.sha256, /^[0-9a-f]{64}$/);
    assert.equal(existsSync(join(root, 'src', 'app', 'shared', 'api', 'hand-written.ts')), false, 'stale generated file survived');
    assert.match(readFileSync(join(root, 'src', 'app', 'shared', 'api', 'fn', 'ping', 'pong.ts'), 'utf8'), /pong/);
  });

  it('leaves no temp directory behind', () => {
    const root = project();
    const leftovers = () => readdirSync(tmpdir()).filter((name) => name.startsWith('nts-api-gen-local-') && !scratch.endsWith(name));

    const before = leftovers().length;
    run(root, input('ok', spec('3.0.0')));
    run(root, input('bad-json', '{ not json'));

    assert.equal(leftovers().length, before);
  });
});
