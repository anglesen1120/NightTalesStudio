// QA (10.5.7-T1) — copy contract of the bible panel: the vi/en key sets are equal, every `series.bible.*` token the
// feature source names resolves in both languages (leaf or dynamic family), and every leaf interpolates its placeholders
// through the `series` scope in both languages.
import { readFileSync, readdirSync } from 'node:fs';
import { join } from 'node:path';
import { TestBed } from '@angular/core/testing';
import { TranslocoService, TranslocoTestingModule } from '@jsverse/transloco';
import { afterEach, describe, expect, it } from 'vitest';
import coreEn from '../../../assets/i18n/core/en.json';
import coreVi from '../../../assets/i18n/core/vi.json';
import errorsEn from '../../../assets/i18n/errors/en.json';
import errorsVi from '../../../assets/i18n/errors/vi.json';
import seriesEn from '../../../assets/i18n/series/en.json';
import seriesVi from '../../../assets/i18n/series/vi.json';

type Leaves = Record<string, string>;

function flatten(node: unknown, prefix = ''): Leaves {
  const leaves: Leaves = {};
  if (typeof node !== 'object' || node === null) return leaves;
  for (const [key, value] of Object.entries(node)) {
    const path = prefix === '' ? key : `${prefix}.${key}`;
    if (typeof value === 'string') leaves[path] = value;
    else Object.assign(leaves, flatten(value, path));
  }
  return leaves;
}

const FEATURE_ROOT = join(process.cwd(), 'src/app/features/series');
const BIBLE_FILES = readdirSync(FEATURE_ROOT, { recursive: true })
  .map((entry) => String(entry))
  .filter((entry) => entry.includes('bible') && (entry.endsWith('.ts') || entry.endsWith('.html')))
  .filter((entry) => !entry.endsWith('.spec.ts'));

const SOURCE_TOKENS = new Set<string>();
for (const relative of BIBLE_FILES) {
  const text = readFileSync(join(FEATURE_ROOT, relative), 'utf8');
  for (const match of text.matchAll(/series\.bible\.[A-Za-z_]+(?:\.[A-Za-z_]+)*/g)) SOURCE_TOKENS.add(match[0]);
  // The banner translates scope-relative keys (`translate('bible.op.summary', {}, 'series')`).
  for (const match of text.matchAll(/'bible\.[A-Za-z_][A-Za-z._]*'/g)) SOURCE_TOKENS.add(`series.${match[0].slice(1, -1)}`);
}
/** Keys assembled at run time (`series.bible.<kind>`, `series.bible.narrator.<field>`, ...). */
const DYNAMIC_KEYS = [
  'series.bible.characters', 'series.bible.places', 'series.bible.rules', 'series.bible.summaries',
  'series.bible.narrator.name', 'series.bible.narrator.persona', 'series.bible.narrator.voiceNotes',
  'series.bible.field.too_many', 'series.bible.field.too_long', 'series.bible.field.duplicate',
  'series.bible.field.unknown_episode', 'series.bible.field.invalid', 'series.bible.field.required', 'series.bible.field.invalidName',
  'series.bible.op.summary', 'series.bible.op.character', 'series.bible.op.place', 'series.bible.op.rule', 'series.bible.op.other',
];

/** Strips the `series.bible.` prefix so a source token can be checked against the scope-relative leaves. */
const relKey = (key: string): string => key.slice('series.bible.'.length);
const VI = flatten(seriesVi.bible);
const EN = flatten(seriesEn.bible);
const KEYS = Object.keys(VI);
const PARAMS: Record<string, string> = { n: '3', title: 'Tiêu đề', field: 'Trường', version: '7', base: '1', current: '2' };

afterEach(() => TestBed.resetTestingModule());

describe('series bible copy (QA)', () => {
  it('has the same bible key set in vi and en, with no empty value', () => {
    expect(KEYS.length).toBeGreaterThan(60);
    expect(Object.keys(EN).sort()).toEqual([...KEYS].sort());
    for (const key of KEYS) {
      expect(VI[key], `vi ${key}`).not.toBe('');
      expect(EN[key], `en ${key}`).not.toBe('');
    }
  });

  it('resolves every series.bible token in the feature source, or proves it is a dynamic family', () => {
    const unresolved: string[] = [];
    for (const token of SOURCE_TOKENS) {
      const leaf = relKey(token);
      const isLeaf = leaf in VI && leaf in EN;
      const isFamily = KEYS.some((key) => key.startsWith(`${leaf}.`)) && Object.keys(EN).some((key) => key.startsWith(`${leaf}.`));
      if (!isLeaf && !isFamily) unresolved.push(token);
    }
    expect([...SOURCE_TOKENS].length, 'the scan finds the literal keys it is meant to check').toBeGreaterThan(30);
    expect(unresolved).toEqual([]);
    expect(DYNAMIC_KEYS.filter((key) => !(relKey(key) in VI) || !(relKey(key) in EN))).toEqual([]);
  });

  it('interpolates every bible key through the series scope in both languages', async () => {
    await TestBed.configureTestingModule({
      imports: [TranslocoTestingModule.forRoot({
        langs: {
          vi: { ...coreVi, series: seriesVi, errors: errorsVi },
          en: { ...coreEn, series: seriesEn, errors: errorsEn },
          'series/vi': seriesVi,
          'series/en': seriesEn,
        },
        translocoConfig: { availableLangs: ['vi', 'en'], defaultLang: 'vi', fallbackLang: 'vi', reRenderOnLangChange: true },
        preloadLangs: true,
      })],
    }).compileComponents();
    const transloco = TestBed.inject(TranslocoService);

    for (const lang of ['vi', 'en'] as const) {
      transloco.setActiveLang(lang);
      const source = lang === 'vi' ? VI : EN;
      for (const key of KEYS) {
        const template = source[key]!;
        const expected = template.replace(/\{\{(\w+)\}\}/g, (_marker, name: string) => PARAMS[name] ?? `<<missing:${name}>>`);
        const resolved = transloco.translate(`bible.${key}`, PARAMS, 'series');
        expect(resolved, `${lang} ${key}`).toBe(expected);
      }
    }
  });
});
