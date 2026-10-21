// Contract test over the vendored backend document web/openapi.json (task 10.1.4-T1; mirrors the backend gate 09.9.5-T1).
import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { readFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { describe, it } from 'node:test';
import { fileURLToPath } from 'node:url';

const webRoot = join(dirname(fileURLToPath(import.meta.url)), '..', '..');
const specBytes = readFileSync(join(webRoot, 'openapi.json'));
const spec = JSON.parse(specBytes.toString('utf8'));
const lock = JSON.parse(readFileSync(join(webRoot, 'tools', 'web', 'openapi.lock.json'), 'utf8'));

const PROBLEM_REF = '#/components/schemas/ProblemDetails';

function* refs(node) {
  if (Array.isArray(node)) {
    for (const item of node) {
      yield* refs(item);
    }
  } else if (node && typeof node === 'object') {
    for (const [key, value] of Object.entries(node)) {
      if (key === '$ref' && typeof value === 'string') {
        yield value;
      } else {
        yield* refs(value);
      }
    }
  }
}

function resolves(ref) {
  if (!ref.startsWith('#/')) {
    return false;
  }
  let node = spec;
  for (const part of ref.slice(2).split('/')) {
    const key = part.replaceAll('~1', '/').replaceAll('~0', '~');
    if (node === null || typeof node !== 'object' || !(key in node)) {
      return false;
    }
    node = node[key];
  }
  return true;
}

const operations = Object.entries(spec.paths).flatMap(([path, item]) =>
  Object.entries(item)
    .filter(([method]) => ['get', 'put', 'post', 'delete', 'patch', 'options', 'head', 'trace'].includes(method))
    .map(([method, operation]) => ({ path, method, operation })),
);

describe('web/openapi.json contract', () => {
  it('is an OpenAPI 3.1 document (09.1.5-T1 recorded 3.1.1)', () => {
    assert.match(spec.openapi, /^3\.1\./);
  });

  it('declares ProblemDetails with type, title, status, code and traceId; code and traceId required', () => {
    const problem = spec.components?.schemas?.ProblemDetails;

    assert.ok(problem, 'components.schemas.ProblemDetails is missing (09.9.5-T1 gate)');
    for (const property of ['type', 'title', 'status', 'code', 'traceId']) {
      assert.ok(property in problem.properties, `ProblemDetails.${property} is missing`);
    }
    assert.ok(problem.required.includes('code') && problem.required.includes('traceId'), 'code and traceId must be required');
    // `errors` (field -> string[]) is optional on the wire; it must never become required if the schema declares it.
    assert.ok(!(problem.required ?? []).includes('errors'), 'errors must stay optional');
  });

  it('references ProblemDetails from every 4xx/5xx/default response', () => {
    const offenders = [];
    for (const { path, method, operation } of operations) {
      for (const [status, response] of Object.entries(operation.responses ?? {})) {
        if (/^[45]\d\d$/.test(status) || status === 'default') {
          if (![...refs(response)].includes(PROBLEM_REF)) {
            offenders.push(`${method.toUpperCase()} ${path} ${status}`);
          }
        }
      }
    }

    assert.deepEqual(offenders, []);
  });

  it('has at least one error response to check (the loop above is not vacuous)', () => {
    const errorResponses = operations.flatMap(({ operation }) => Object.keys(operation.responses ?? {})).filter((status) => /^[45]\d\d$/.test(status));

    assert.ok(errorResponses.length > 0);
  });

  it('resolves every $ref inside the document', () => {
    const unresolved = [...new Set(refs(spec))].filter((ref) => !resolves(ref));

    assert.deepEqual(unresolved, []);
  });

  it('has no operation tag that ng-openapi-gen 1.1.0 cannot import, other than the ones api-gen.config.json excludes', () => {
    // The generator treats a dotted tag ("NightTales.Api", the tag ASP.NET gives a lambda handler: the assembly name)
    // as a qualified name, writes the service into a namespace folder and emits imports for the flat layout, so the
    // client does not compile. The fix is an explicit WithTags on the endpoint (backend, 09.x); until then the tag is
    // listed in excludeTags and its operations (POST /api/v1/auth/register, GET /health/live) have no generated client.
    const config = JSON.parse(readFileSync(join(webRoot, 'api-gen.config.json'), 'utf8'));
    const excluded = new Set(config.excludeTags ?? []);
    const offenders = operations.flatMap(({ path, method, operation }) =>
      (operation.tags ?? []).filter((tag) => tag.includes('.') && !excluded.has(tag)).map((tag) => `${method.toUpperCase()} ${path} [${tag}]`),
    );

    assert.deepEqual(offenders, []);
  });

  it('bakes no absolute server origin (README D3: the SPA calls relative URLs)', () => {
    const absolute = (spec.servers ?? []).map((server) => server.url).filter((url) => /^[a-z][a-z0-9+.-]*:\/\//i.test(url));

    assert.deepEqual(absolute, []);
  });

  it('has a lock whose SHA-256 and info.version match the vendored document', () => {
    assert.equal(lock.source, 'artifact openapi (09.1.5-T1)');
    assert.equal(typeof spec.info.version, 'string');
    assert.ok(spec.info.version.length > 0, 'info.version must be present');
    assert.equal(lock.specVersion, spec.info.version);
    assert.equal(lock.sha256, createHash('sha256').update(specBytes).digest('hex'));
    assert.match(lock.sourceCommit, /^[0-9a-f]{40}$/, 'lock.sourceCommit must record the backend commit (40-hex sha)');
  });
});
