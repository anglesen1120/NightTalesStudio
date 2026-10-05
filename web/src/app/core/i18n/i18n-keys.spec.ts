import { existsSync, readFileSync, readdirSync } from 'node:fs';
import { join } from 'node:path';
import {
  HANDED_OVER_KEYS,
  checkHandedOverKeys,
  checkScopeFiles,
  checkUsedKeys,
  diffKeySets,
  findDuplicateLeaves,
  flattenKeys,
  matchesPattern,
  nonStringLeaves,
  runtimeKey,
  scopeAlias,
  type ScopeKeys,
} from '../../../../tools/web/i18n-keys.mjs';

const I18N_ROOT = join(process.cwd(), 'src/assets/i18n');

function readScope(scope: string): { vi: string | null; en: string | null } {
  const read = (lang: string) => {
    const path = join(I18N_ROOT, scope, `${lang}.json`);
    return existsSync(path) ? readFileSync(path, 'utf8') : null;
  };
  return { vi: read('vi'), en: read('en') };
}

const SYNTHETIC: Record<string, ScopeKeys> = {
  series: {
    vi: ['style.trueCrimeNotice', 'style.documentary.label', 'style.documentary.hint'],
    en: ['style.trueCrimeNotice', 'style.documentary.label', 'style.documentary.hint'],
  },
  voices: { vi: ['tag.male'], en: ['tag.male'] },
};

describe('flattenKeys / diffKeySets', () => {
  it('flattens nested objects to sorted dot paths', () => {
    expect(flattenKeys({ b: { y: 'x', x: 'x' }, a: 'x' })).toEqual(['a', 'b.x', 'b.y']);
  });

  it('returns nothing for empty objects and treats empty nested objects as having no leaves', () => {
    expect(flattenKeys({})).toEqual([]);
    expect(flattenKeys({ a: {} })).toEqual([]);
  });

  it('treats arrays and null as leaves, and nonStringLeaves reports them', () => {
    const value = { a: ['x'], b: null, c: 1, d: 'ok', e: { f: false } };
    expect(flattenKeys(value)).toEqual(['a', 'b', 'c', 'd', 'e.f']);
    expect(nonStringLeaves(value)).toEqual(['a', 'b', 'c', 'e.f']);
  });

  it('diffKeySets reports both directions', () => {
    expect(diffKeySets(['a', 'b'], ['b', 'c'])).toEqual({ onlyA: ['a'], onlyB: ['c'] });
    expect(diffKeySets(['a'], ['a'])).toEqual({ onlyA: [], onlyB: [] });
  });
});

describe('findDuplicateLeaves', () => {
  it('finds a hand-made duplicate leaf and its nested path', () => {
    const text = '{ "common": { "close": "A", "retry": "B", "close": "C" }, "x": "1", "x": "2" }';
    expect(findDuplicateLeaves(text)).toEqual(['common.close', 'x']);
  });

  it('does not flag the same key name under different parents or inside arrays', () => {
    expect(findDuplicateLeaves('{ "a": { "k": "1" }, "b": { "k": "2" }, "l": [ { "k": "1" }, { "k": "2" } ] }')).toEqual([]);
  });

  it('ignores braces, commas and colons inside strings, and escaped quotes', () => {
    expect(findDuplicateLeaves('{ "a": "x, { \\" y: z", "b": "}", "a2": "w" }')).toEqual([]);
  });
});

describe('matchesPattern', () => {
  it('accepts a concrete key and a one-segment wildcard', () => {
    expect(matchesPattern('series.style.trueCrimeNotice', 'series.style.trueCrimeNotice')).toBe(true);
    expect(matchesPattern('voices.tag.male', 'voices.tag.*')).toBe(true);
    expect(matchesPattern('series.style.documentary.label', 'series.style.*.label')).toBe(true);
  });

  it('normalises <name> placeholders to *', () => {
    expect(matchesPattern('voices.tag.male', 'voices.tag.<tag>')).toBe(true);
    expect(matchesPattern('series.style.horror.hint', 'series.style.<name>.hint')).toBe(true);
  });

  it('* matches exactly one segment: not zero, not two', () => {
    expect(matchesPattern('voices.tag', 'voices.tag.*')).toBe(false);
    expect(matchesPattern('voices.tag.a.b', 'voices.tag.*')).toBe(false);
    expect(matchesPattern('series.style.label', 'series.style.*.label')).toBe(false);
    expect(matchesPattern('series.style.a.b.label', 'series.style.*.label')).toBe(false);
  });

  it('a different literal segment does not match', () => {
    expect(matchesPattern('series.style.documentary.title', 'series.style.*.label')).toBe(false);
  });
});

describe('scope naming', () => {
  it('camel-cases the scope alias Transloco prefixes keys with', () => {
    expect(scopeAlias('quality')).toBe('quality');
    expect(scopeAlias('my-scope')).toBe('myScope');
  });

  it('core keys are used as written, other scopes are alias-prefixed', () => {
    expect(runtimeKey('core', 'common.close')).toBe('common.close');
    expect(runtimeKey('quality', 'hook_missing')).toBe('quality.hook_missing');
  });
});

describe('checkScopeFiles', () => {
  const ok = '{ "a": { "b": "x" }, "c": "y" }';

  it('is silent for equal key sets', () => {
    expect(checkScopeFiles('s', { vi: ok, en: ok }).problems).toEqual([]);
  });

  it('a key deleted from vi fails as "<scope>/vi: missing key"', () => {
    const { problems } = checkScopeFiles('core', { vi: '{ "a": { "b": "x" } }', en: ok });
    expect(problems).toEqual(['core/vi: missing key c']);
  });

  it('a key deleted from en fails as "<scope>/en: missing key"', () => {
    const { problems } = checkScopeFiles('core', { vi: ok, en: '{ "c": "y" }' });
    expect(problems).toEqual(['core/en: missing key a.b']);
  });

  it('a missing language file fails as "<scope>: missing language file"', () => {
    expect(checkScopeFiles('core', { vi: ok, en: null }).problems).toEqual(['core: missing language file (en.json)']);
    expect(checkScopeFiles('core', { vi: null, en: null }).problems).toHaveLength(2);
  });

  it('duplicate and non-string leaves fail', () => {
    const duplicated = checkScopeFiles('s', { vi: '{ "a": "1", "a": "2" }', en: '{ "a": "1" }' }).problems;
    expect(duplicated).toEqual(['s: duplicate leaf a (vi.json)']);
    const nonString = checkScopeFiles('s', { vi: '{ "a": 1, "b": null }', en: '{ "a": "x", "b": "y" }' }).problems;
    expect(nonString).toEqual(['s/vi: non-string leaf a', 's/vi: non-string leaf b']);
  });

  it('invalid JSON and a non-object root fail without throwing', () => {
    expect(checkScopeFiles('s', { vi: '{', en: ok }).problems[0]).toMatch(/^s\/vi: invalid JSON/);
    expect(checkScopeFiles('s', { vi: '[]', en: ok }).problems).toEqual(['s/vi: root must be a JSON object']);
  });
});

describe('checkUsedKeys', () => {
  const scopes: Record<string, ScopeKeys> = {
    core: { vi: ['common.close', 'only.vi'], en: ['common.close', 'only.en'] },
    quality: { vi: ['hook_missing'], en: ['hook_missing'] },
  };

  it('reports a used key with no entry and names the file', () => {
    expect(checkUsedKeys([{ scope: 'core', key: 'shell.doesNotExist', file: 'src/app/x.ts' }], scopes)).toEqual([
      'used but undefined: shell.doesNotExist (src/app/x.ts)',
    ]);
  });

  it('stays silent when the key exists in either language', () => {
    expect(
      checkUsedKeys(
        [
          { scope: 'core', key: 'common.close' },
          { scope: 'core', key: 'only.vi' },
          { scope: 'core', key: 'only.en' },
          { scope: 'quality', key: 'hook_missing' },
        ],
        scopes,
      ),
    ).toEqual([]);
  });

  it('reports the runtime key (scope alias prefix) and a scope without files', () => {
    expect(checkUsedKeys([{ scope: 'quality', key: 'nope' }, { scope: 'series', key: 'style.x' }], scopes)).toEqual([
      'used but undefined: quality.nope',
      'used but undefined: series.style.x',
    ]);
  });
});

describe('HANDED_OVER_KEYS', () => {
  it('declares every handed-over family from README D10 with its owning scope', () => {
    const owned = HANDED_OVER_KEYS.map((entry) => `${entry.scope}:${entry.pattern}`);
    expect(owned).toEqual([
      'series:series.style.trueCrimeNotice',
      'series:series.style.*.label',
      'series:series.style.*.hint',
      'voices:voices.tag.*',
      'quality:quality.*',
      'render:render.*',
    ]);
  });

  it('is satisfied by a synthetic two-language scope', () => {
    expect(checkHandedOverKeys(SYNTHETIC)).toEqual([]);
  });

  it('is reported when an existing scope lacks a handed-over key', () => {
    const missingTag = { ...SYNTHETIC, voices: { vi: ['other.male'], en: ['other.male'] } };
    expect(checkHandedOverKeys(missingTag)).toEqual(['voices: handed-over key voices.tag.* not found']);
  });

  it('matches the scope-prefixed patterns on the runtime key only: a file repeating the scope prefix is reported', () => {
    const repeated = ['series.style.trueCrimeNotice', 'series.style.documentary.label', 'series.style.documentary.hint'];
    expect(checkHandedOverKeys({ series: { vi: repeated, en: repeated } })).toEqual([
      'series: handed-over key series.style.trueCrimeNotice not found',
      'series: handed-over key series.style.*.label not found',
      'series: handed-over key series.style.*.hint not found',
    ]);
  });

  it('is never red while the owning scope directory is absent', () => {
    expect(checkHandedOverKeys({})).toEqual([]);
    expect(checkHandedOverKeys({ core: { vi: ['common.close'], en: ['common.close'] } })).toEqual([]);
  });

  it('quality.* is satisfied by the real flat hint codes (alias prefix) and matches one segment only', () => {
    expect(checkHandedOverKeys({ quality: { vi: ['hook_missing'], en: ['hook_missing'] } })).toEqual([]);
    expect(checkHandedOverKeys({ quality: { vi: ['a.b'], en: ['a.b'] } })).toEqual(['quality: handed-over key quality.* not found']);
  });
});

describe('the committed scope tree', () => {
  const scopes = readdirSync(I18N_ROOT, { withFileTypes: true })
    .filter((entry) => entry.isDirectory())
    .map((entry) => entry.name);

  it('has the core scope and the quality scope', () => {
    expect(scopes).toContain('core');
    expect(scopes).toContain('quality');
  });

  for (const scope of scopes) {
    it(`${scope}: vi and en are equal, duplicate-free and string-only`, () => {
      const { problems } = checkScopeFiles(scope, readScope(scope));
      expect(problems).toEqual([]);
    });
  }

  it('satisfies the handed-over patterns of the scopes that exist', () => {
    const keys: Record<string, ScopeKeys> = {};
    for (const scope of scopes) keys[scope] = checkScopeFiles(scope, readScope(scope)).keys;
    expect(checkHandedOverKeys(keys)).toEqual([]);
  });

  it('keeps the kit keys and the common.* copy intact in core', () => {
    const { keys } = checkScopeFiles('core', readScope('core'));
    expect(keys.vi).toContain('core.ui.pageHeader.back');
    for (const key of ['close', 'retry', 'cancel', 'back', 'loading', 'translationUnavailable']) {
      expect(keys.vi).toContain(`common.${key}`);
      expect(keys.en).toContain(`common.${key}`);
    }
  });

  it('keeps core/{vi,en}.json within the 4 kB raw budget each', () => {
    for (const lang of ['vi', 'en']) {
      expect(Buffer.byteLength(readScope('core')[lang as 'vi' | 'en'] ?? '')).toBeLessThanOrEqual(4096);
    }
  });
});
