import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { mkdirSync, mkdtempSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { dirname, join } from 'node:path';
import { after, describe, it } from 'node:test';
import { fileURLToPath } from 'node:url';
import { randomBytes } from 'node:crypto';
import { gzipSync } from 'node:zlib';
import { BUDGET_BYTES, initialReferences } from './check-gzip-budget.mjs';

const SCRIPT = join(dirname(fileURLToPath(import.meta.url)), 'check-gzip-budget.mjs');
const scratch = mkdtempSync(join(tmpdir(), 'nts-gzip-budget-'));
after(() => rmSync(scratch, { recursive: true, force: true }));

/** Builds a fixture build-output directory whose single script gzips to a known size. */
function fixture(name, { script, extraHtml = '', html = 'index.csr.html' }) {
  const dir = join(scratch, name);
  mkdirSync(dir, { recursive: true });
  writeFileSync(join(dir, 'main.js'), script);
  writeFileSync(
    join(dir, html),
    `<!doctype html><html><head><link rel="stylesheet" href="styles.css"></head><body>${extraHtml}<script src="main.js" type="module"></script></body></html>`,
  );
  writeFileSync(join(dir, 'styles.css'), 'body{margin:0}');
  return dir;
}

function run(...args) {
  const result = spawnSync(process.execPath, [SCRIPT, ...args], { encoding: 'utf8' });
  return { code: result.status, out: result.stdout, err: result.stderr };
}

const gzipSize = (buffer) => gzipSync(buffer, { level: 9 }).length;

describe('check-gzip-budget', () => {
  it('names the budget as 250 KiB (README D13 "250 kB")', () => {
    assert.equal(BUDGET_BYTES, 256_000);
  });

  it('exits 0 and reports the gzip byte count for an under-budget build', () => {
    const script = Buffer.from('export const a = 1;\n'.repeat(500));
    const dir = fixture('under', { script });
    const expected = gzipSize(script) + gzipSize(Buffer.from('body{margin:0}'));

    const { code, out } = run('--dir', dir);

    assert.equal(code, 0, out);
    assert.match(out, new RegExp(`total gzip ${expected} bytes \\(budget ${BUDGET_BYTES}\\)`));
    assert.match(out, new RegExp(`main\\.js ${script.length} ${gzipSize(script)}`));
  });

  it('exits 1 and prints the overage for an over-budget build', () => {
    // Random bytes do not compress, so the gzip size is known to exceed the budget.
    const script = randomBytes(BUDGET_BYTES + 5_000);
    const dir = fixture('over', { script });
    const total = gzipSize(script) + gzipSize(Buffer.from('body{margin:0}'));

    const { code, out, err } = run('--dir', dir);

    assert.equal(code, 1);
    assert.match(out, new RegExp(`total gzip ${total} bytes`));
    assert.match(err, new RegExp(`exceeds the budget by ${total - BUDGET_BYTES} bytes`));
  });

  it('treats a total exactly at the budget as passing and one byte over as failing', () => {
    const script = Buffer.from('x'.repeat(2_000));
    const dir = fixture('boundary', { script });
    const total = gzipSize(script) + gzipSize(Buffer.from('body{margin:0}'));

    assert.equal(run('--dir', dir, '--budget', String(total)).code, 0);
    assert.equal(run('--dir', dir, '--budget', String(total - 1)).code, 1);
  });

  it('fails loudly (exit 1, message) when the build directory is missing', () => {
    const { code, err } = run('--dir', join(scratch, 'does-not-exist'));

    assert.equal(code, 1);
    assert.match(err, /build output directory not found/);
  });

  it('fails when the document references a file that is not in the build output', () => {
    const dir = fixture('dangling', { script: 'x', extraHtml: '<script src="gone.js"></script>' });

    const { code, err } = run('--dir', dir);

    assert.equal(code, 1);
    assert.match(err, /gone\.js.*does not exist/);
  });

  it('fails instead of reporting 0 bytes when the document loads nothing', () => {
    const dir = join(scratch, 'empty-document');
    mkdirSync(dir, { recursive: true });
    writeFileSync(join(dir, 'index.csr.html'), '<!doctype html><html><body></body></html>');

    const { code, err } = run('--dir', dir);

    assert.equal(code, 1);
    assert.match(err, /references no initial script or stylesheet/);
  });

  it('rejects an invalid --budget', () => {
    const dir = fixture('bad-budget', { script: 'x' });

    assert.equal(run('--dir', dir, '--budget', 'abc').code, 1);
    assert.equal(run('--dir', dir, '--budget', '0').code, 1);
  });

  it('collects script src, modulepreload and stylesheet references once each', () => {
    const html = `
      <link rel="modulepreload" href="chunk-A.js">
      <link rel="stylesheet" href="styles.css">
      <link rel="icon" href="favicon.ico">
      <link rel="preload" as="font" href="font.woff2">
      <script src="main.js" type="module"></script>
      <script>inline()</script>
      <script src="main.js"></script>`;

    assert.deepEqual([...initialReferences(html)].sort(), ['chunk-A.js', 'main.js', 'styles.css']);
  });

  it('does not count a lazy chunk the document never references, but counts a modulepreloaded one', () => {
    const script = Buffer.from('export const a = 1;\n'.repeat(500));
    const lazy = randomBytes(BUDGET_BYTES + 5_000);
    const dir = fixture('lazy', { script });
    writeFileSync(join(dir, 'chunk-LAZY.js'), lazy);
    const base = gzipSize(script) + gzipSize(Buffer.from('body{margin:0}'));

    const unreferenced = run('--dir', dir);
    assert.equal(unreferenced.code, 0, unreferenced.out);
    assert.match(unreferenced.out, new RegExp(`total gzip ${base} bytes`));
    assert.doesNotMatch(unreferenced.out, /chunk-LAZY/);

    const preloadedDir = fixture('preloaded', {
      script,
      extraHtml: '<link rel="modulepreload" href="chunk-INITIAL.js">',
    });
    writeFileSync(join(preloadedDir, 'chunk-INITIAL.js'), lazy);
    const preloaded = run('--dir', preloadedDir);
    assert.equal(preloaded.code, 1);
    assert.match(preloaded.out, new RegExp(`chunk-INITIAL\\.js ${lazy.length} ${gzipSize(lazy)}`));
    assert.match(preloaded.err, /exceeds the budget by/);
  });
});
