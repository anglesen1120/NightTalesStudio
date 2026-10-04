// Runs the real `check-i18n.mjs` CLI (with the pinned keys-manager) against throwaway trees: the green exit and
// every failure exit of 10.2.5-T1 are proven, not only documented. Run by `npm run test:tools`.

import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { mkdirSync, mkdtempSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { dirname, join } from 'node:path';
import { after, describe, test } from 'node:test';
import { fileURLToPath } from 'node:url';

const SCRIPT = join(dirname(fileURLToPath(import.meta.url)), 'check-i18n.mjs');
const roots = [];

after(() => {
  for (const root of roots) rmSync(root, { recursive: true, force: true });
});

/** @param {Record<string, string>} files paths relative to the throwaway web root */
function tree(files) {
  const root = mkdtempSync(join(tmpdir(), 'nts-check-i18n-'));
  roots.push(root);
  mkdirSync(join(root, 'src', 'app'), { recursive: true });
  for (const [path, text] of Object.entries(files)) {
    mkdirSync(dirname(join(root, path)), { recursive: true });
    writeFileSync(join(root, path), text);
  }
  return root;
}

function run(root) {
  const result = spawnSync(process.execPath, [SCRIPT, '--root', root], { encoding: 'utf8' });
  return { status: result.status, out: `${result.stdout}${result.stderr}` };
}

const CORE = '{ "common": { "close": "Đóng", "retry": "Thử lại" } }';
const CORE_EN = '{ "common": { "close": "Close", "retry": "Retry" } }';
const i18n = (scope, vi, en) => ({
  ...(vi === null ? {} : { [`src/assets/i18n/${scope}/vi.json`]: vi }),
  ...(en === null ? {} : { [`src/assets/i18n/${scope}/en.json`]: en }),
});
const template = (key) => ({ 'src/app/page.component.html': `<p>{{ '${key}' | transloco }}</p>\n` });
const scopedComponent = (scope, key) => ({
  'src/app/page.component.ts': `import { Component } from '@angular/core';\nimport { TranslocoPipe, provideTranslocoScope } from '@jsverse/transloco';\n@Component({ imports: [TranslocoPipe], providers: [provideTranslocoScope('${scope}')], template: \`<p>{{ '${key}' | transloco }}</p>\` })\nexport class Page {}\n`,
});

describe('i18n:check CLI', () => {
  test('empty assets tree: green, zero everything', () => {
    const { status, out } = run(tree({}));
    assert.equal(status, 0, out);
    assert.match(out, /i18n ok: 0 scopes, 0 keys, 0 used keys/);
  });

  test('consistent tree: green and counts scopes, keys and used keys', () => {
    const { status, out } = run(tree({ ...i18n('core', CORE, CORE_EN), ...template('common.close') }));
    assert.equal(status, 0, out);
    assert.match(out, /i18n ok: 1 scopes, 2 keys, 1 used keys/);
  });

  test('a key deleted from core/vi.json exits 1 with "core/vi: missing key"', () => {
    const { status, out } = run(tree(i18n('core', '{ "common": { "close": "Đóng" } }', CORE_EN)));
    assert.equal(status, 1);
    assert.match(out, /core\/vi: missing key common\.retry/);
  });

  test('a key deleted from core/en.json exits 1 with "core/en: missing key"', () => {
    const { status, out } = run(tree(i18n('core', CORE, '{ "common": { "close": "Close" } }')));
    assert.equal(status, 1);
    assert.match(out, /core\/en: missing key common\.retry/);
  });

  test('removing core/en.json exits 1 with "core: missing language file"', () => {
    const { status, out } = run(tree(i18n('core', CORE, null)));
    assert.equal(status, 1);
    assert.match(out, /core: missing language file/);
  });

  test('a duplicate leaf exits 1 with "duplicate leaf"', () => {
    const { status, out } = run(tree(i18n('core', '{ "a": "1", "a": "2" }', '{ "a": "1" }')));
    assert.equal(status, 1);
    assert.match(out, /core: duplicate leaf a/);
  });

  test('a non-string leaf exits 1 with "non-string leaf"', () => {
    const { status, out } = run(tree(i18n('core', '{ "a": 1 }', '{ "a": "x" }')));
    assert.equal(status, 1);
    assert.match(out, /core\/vi: non-string leaf a/);
  });

  test('a stray file outside a scope directory and an unexpected language file exit 1', () => {
    const { status, out } = run(
      tree({ ...i18n('core', CORE, CORE_EN), 'src/assets/i18n/vi.json': '{}', 'src/assets/i18n/core/fr.json': '{}' }),
    );
    assert.equal(status, 1);
    assert.match(out, /unexpected file outside a scope directory/);
    assert.match(out, /core: unexpected file fr\.json/);
  });

  test('a key used in a template with no JSON entry exits 1 with "used but undefined"', () => {
    const { status, out } = run(tree({ ...i18n('core', CORE, CORE_EN), ...template('shell.doesNotExist') }));
    assert.equal(status, 1);
    assert.match(out, /used but undefined: shell\.doesNotExist \(src\/app\/page\.component\.html\)/);
  });

  test('a scoped used key is checked in its own scope (defined: green, undefined: red)', () => {
    const quality = i18n('quality', '{ "hook_missing": "Thiếu hook" }', '{ "hook_missing": "No hook" }');
    const defined = run(tree({ ...i18n('core', CORE, CORE_EN), ...quality, ...scopedComponent('quality', 'quality.hook_missing') }));
    assert.equal(defined.status, 0, defined.out);
    const undef = run(tree({ ...i18n('core', CORE, CORE_EN), ...quality, ...scopedComponent('quality', 'quality.nope') }));
    assert.equal(undef.status, 1);
    assert.match(undef.out, /used but undefined: quality\.nope/);
  });

  test('handed-over keys: red only while the owning scope exists without a match', () => {
    const absent = run(tree(i18n('core', CORE, CORE_EN)));
    assert.equal(absent.status, 0, absent.out);

    const satisfied = run(tree(i18n('quality', '{ "hook_missing": "a" }', '{ "hook_missing": "b" }')));
    assert.equal(satisfied.status, 0, satisfied.out);

    const unmatched = run(tree(i18n('quality', '{ "a": { "b": "x" } }', '{ "a": { "b": "y" } }')));
    assert.equal(unmatched.status, 1);
    assert.match(unmatched.out, /quality: handed-over key quality\.\* not found/);

    const series = run(
      tree(
        i18n(
          'series',
          '{ "style": { "trueCrimeNotice": "a", "doc": { "label": "l", "hint": "h" } }, "voices": { "tag": { "male": "m" } } }',
          '{ "style": { "trueCrimeNotice": "a", "doc": { "label": "l", "hint": "h" } }, "voices": { "tag": { "male": "m" } } }',
        ),
      ),
    );
    assert.equal(series.status, 0, series.out);
  });

  test('the committed tree is green', () => {
    const result = spawnSync(process.execPath, [SCRIPT], { encoding: 'utf8' });
    assert.equal(result.status, 0, `${result.stdout}${result.stderr}`);
    assert.match(result.stdout, /^i18n ok: \d+ scopes, \d+ keys, \d+ used keys/);
  });
});
