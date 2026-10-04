import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { cpSync, mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { dirname, join } from 'node:path';
import { after, describe, it } from 'node:test';
import { fileURLToPath } from 'node:url';
import { compareTrees, scanGuards, stripComments, unsupportedConfigKeys } from './check-api-drift.mjs';

const toolsDir = dirname(fileURLToPath(import.meta.url));
const webRoot = join(toolsDir, '..', '..');
const SCRIPT = join(toolsDir, 'check-api-drift.mjs');
const GENERATOR = join(webRoot, 'node_modules', 'ng-openapi-gen', 'lib', 'index.js');

const scratch = mkdtempSync(join(tmpdir(), 'nts-api-drift-test-'));
after(() => rmSync(scratch, { recursive: true, force: true }));

let counter = 0;
/** A fresh directory under the scratch root, filled from `files` (relative path -> content). */
function tree(files) {
  counter += 1;
  const dir = join(scratch, `tree-${counter}`);
  for (const [file, content] of Object.entries(files)) {
    mkdirSync(dirname(join(dir, file)), { recursive: true });
    writeFileSync(join(dir, file), content);
  }
  return dir;
}

const BARREL = "export type { ProblemDetails } from './models/problem-details';\n";
const MODEL = 'export interface ProblemDetails {\n  code: string;\n  traceId: string;\n}\n';
const baseline = () => ({ 'index.ts': BARREL, 'models/problem-details.ts': MODEL });

describe('compareTrees', () => {
  it('(a) identical trees: no added, removed or changed file', () => {
    const result = compareTrees(tree(baseline()), tree(baseline()));

    assert.deepEqual(result, { total: 2, added: [], removed: [], changed: [] });
  });

  it('(b) one byte changed in a model: that file is reported with the first differing line', () => {
    const edited = baseline();
    edited['models/problem-details.ts'] = MODEL.replace('traceId: string', 'traceId: number');

    const { changed } = compareTrees(tree(baseline()), tree(edited));

    assert.equal(changed.length, 1);
    assert.equal(changed[0].file, 'models/problem-details.ts');
    assert.equal(changed[0].line, 3);
    assert.equal(changed[0].expected, '  traceId: string;');
    assert.equal(changed[0].actual, '  traceId: number;');
  });

  it('(c) one committed file removed: reported as missing; an extra committed file as stale', () => {
    const generated = tree(baseline());
    const committedWithoutModel = tree({ 'index.ts': BARREL });
    const committedWithExtra = tree({ ...baseline(), 'models/hand-written.ts': 'export {};\n' });

    assert.deepEqual(compareTrees(generated, committedWithoutModel).added, ['models/problem-details.ts']);
    assert.deepEqual(compareTrees(generated, committedWithExtra).removed, ['models/hand-written.ts']);
  });

  it('treats a missing committed directory as an empty tree (nothing committed yet is drift)', () => {
    const { added } = compareTrees(tree(baseline()), join(scratch, 'does-not-exist'));

    assert.deepEqual(added, ['index.ts', 'models/problem-details.ts']);
  });
});

describe('scanGuards', () => {
  it('passes a clean tree', () => {
    assert.deepEqual(scanGuards(tree(baseline())), []);
  });

  it('(d) an absolute URL in a string literal fires the absolute-URL guard with file and line', () => {
    const dir = tree({ ...baseline(), 'services/x-client.ts': "const a = 1;\nexport const root = 'https://api.example/v1';\n" });

    const findings = scanGuards(dir);

    assert.equal(findings.length, 1);
    assert.equal(findings[0].kind, 'absolute-url');
    assert.equal(findings[0].file, 'services/x-client.ts');
    assert.equal(findings[0].line, 2);
    assert.match(findings[0].message, /absolute http\(s\):\/\/ URL/);
  });

  it('(d) a template literal and an http:// URL are caught too', () => {
    const dir = tree({ ...baseline(), 'a.ts': 'export const a = `http://insecure.example/${1}`;\n' });

    assert.deepEqual(scanGuards(dir).map((finding) => finding.kind), ['absolute-url']);
  });

  it('ignores URLs in comments (the generator links a GitHub issue in request-builder.ts)', () => {
    const dir = tree({
      ...baseline(),
      'request-builder.ts': "/**\n * See https://github.com/angular/angular/issues/18261\n */\n// also https://example.test\nexport const ok = 1;\n",
    });

    assert.deepEqual(scanGuards(dir), []);
  });

  it('(e) an index.ts without ProblemDetails fires the shape guard', () => {
    const dir = tree({ 'index.ts': "export type { Other } from './models/other';\n", 'models/other.ts': 'export interface Other {}\n' });

    const findings = scanGuards(dir);

    assert.deepEqual(findings.map((finding) => finding.kind), ['shape']);
    assert.match(findings[0].message, /ProblemDetails/);
  });

  it('(e) a missing index.ts fires the shape guard', () => {
    assert.deepEqual(scanGuards(tree({ 'models/x.ts': 'export {};\n' })).map((finding) => finding.kind), ['shape']);
  });

  it('accepts the value and the type export forms of ProblemDetails', () => {
    assert.deepEqual(scanGuards(tree({ 'index.ts': "export { ProblemDetails } from './p';\n" })), []);
    assert.deepEqual(scanGuards(tree({ 'index.ts': "export type { A, ProblemDetails, B } from './p';\n" })), []);
  });
});

describe('stripComments', () => {
  it('removes line and block comments, keeps strings and line numbers', () => {
    const source = "a // https://x\nb /* https://y\n z */ c\nconst s = '// https://kept';\n";

    assert.equal(stripComments(source), "a \nb \n c\nconst s = '// https://kept';\n");
  });

  it('handles escaped quotes inside strings', () => {
    assert.equal(stripComments("const s = 'it\\'s // not a comment';\n"), "const s = 'it\\'s // not a comment';\n");
  });
});

describe('unsupportedConfigKeys', () => {
  const schema = JSON.parse(readFileSync(join(webRoot, 'node_modules', 'ng-openapi-gen', 'ng-openapi-gen-schema.json'), 'utf8'));

  it('accepts the committed api-gen.config.json', () => {
    const config = JSON.parse(readFileSync(join(webRoot, 'api-gen.config.json'), 'utf8'));

    assert.deepEqual(unsupportedConfigKeys(config, schema), []);
  });

  it('rejects an option the pinned generator does not know instead of ignoring it', () => {
    assert.deepEqual(unsupportedConfigKeys({ input: 'x', moduleStyle: 'standalone' }, schema), ['moduleStyle']);
  });
});

describe('check-api-drift CLI (real generator, tiny spec)', () => {
  const spec = {
    openapi: '3.1.1',
    info: { title: 'fixture', version: '9.9.9' },
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
  };

  /** A web root with `spec`, the real config and a committed client generated from it. */
  function project(specDocument = spec) {
    counter += 1;
    const root = join(scratch, `project-${counter}`);
    mkdirSync(root, { recursive: true });
    writeFileSync(join(root, 'openapi.json'), JSON.stringify(specDocument));
    cpSync(join(webRoot, 'api-gen.config.json'), join(root, 'api-gen.config.json'));
    const generated = spawnSync(process.execPath, [GENERATOR, '--config', 'api-gen.config.json'], { cwd: root, encoding: 'utf8' });
    assert.equal(generated.status, 0, generated.stdout + generated.stderr);
    return root;
  }

  const run = (root) => {
    const result = spawnSync(process.execPath, [SCRIPT, '--web-root', root], { encoding: 'utf8' });
    return { code: result.status, out: result.stdout, err: result.stderr };
  };

  it('exits 0 and prints ok: N files for the committed state', () => {
    const { code, out } = run(project());

    assert.equal(code, 0, out);
    assert.match(out, /^ok: \d+ files$/m);
  });

  it('exits 1 with a file-level summary when a generated file is mutated', () => {
    const root = project();
    const model = join(root, 'src', 'app', 'shared', 'api', 'models', 'problem-details.ts');
    writeFileSync(model, `${readFileSync(model, 'utf8')}// hand edit\n`);

    const { code, out, err } = run(root);

    assert.equal(code, 1);
    assert.match(out, /^drift: 1 files$/m);
    assert.match(err, /changed {2}models\/problem-details\.ts:\d+/);
    assert.match(out, /api:check failed: drift/);
  });

  it('exits 1 naming a committed file the generator no longer produces', () => {
    const root = project();
    writeFileSync(join(root, 'src', 'app', 'shared', 'api', 'hand-written.ts'), 'export {};\n');

    const { code, err } = run(root);

    assert.equal(code, 1);
    assert.match(err, /stale {4}hand-written\.ts/);
  });

  it('exits 1 with the ProblemDetails message when the spec stops declaring it', () => {
    const root = project();
    const without = structuredClone(spec);
    delete without.components.schemas.ProblemDetails;
    delete without.paths['/api/v1/ping'].get.responses[400];
    writeFileSync(join(root, 'openapi.json'), JSON.stringify(without));

    const { code, err, out } = run(root);

    assert.equal(code, 1);
    assert.match(err, /shape: .*does not export the ProblemDetails model/);
    assert.match(out, /api:check failed: .*shape/);
  });

  it('exits 1 naming the path when openapi.json is missing, before generating anything', () => {
    const root = project();
    rmSync(join(root, 'openapi.json'));

    const { code, err, out } = run(root);

    assert.equal(code, 1);
    assert.match(err, /openapi\.json is missing: .*openapi\.json/);
    assert.match(out, /api:check failed: spec/);
  });

  it('exits 1 when openapi.json is not parsable JSON', () => {
    const root = project();
    writeFileSync(join(root, 'openapi.json'), '{ not json');

    const { code, err } = run(root);

    assert.equal(code, 1);
    assert.match(err, /openapi\.json is not valid JSON/);
  });

  it('exits 1 when the generator config carries an unsupported option', () => {
    const root = project();
    const configPath = join(root, 'api-gen.config.json');
    writeFileSync(configPath, JSON.stringify({ ...JSON.parse(readFileSync(configPath, 'utf8')), moduleStyle: 'standalone' }));

    const { code, err } = run(root);

    assert.equal(code, 1);
    assert.match(err, /does not support: moduleStyle/);
  });

  it('never touches the committed tree while checking', () => {
    const root = project();
    const model = join(root, 'src', 'app', 'shared', 'api', 'models', 'problem-details.ts');
    const edited = `${readFileSync(model, 'utf8')}// hand edit\n`;
    writeFileSync(model, edited);

    run(root);

    assert.equal(readFileSync(model, 'utf8'), edited);
  });
});
