// QA (10.5.7-T1) — copy contract of the bible panel: the vi/en key sets are equal, every `series.bible.*` token the
// feature source names resolves in both languages (leaf or dynamic family), every leaf interpolates its placeholders
// through the `series` scope in both languages, and the 02.6.3 §6 hand-over copy is verbatim.
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

  it('keeps the 02.6.3 §6 hand-over copy verbatim in both languages', () => {
    expect(VI['title']).toBe('Bộ nhớ của bộ truyện');
    expect(EN['title']).toBe('Series bible');
    expect(VI['empty']).toBe('Truyện này chưa ghi nhớ gì. Nó sẽ tự đầy lên sau mỗi tập được duyệt.');
    expect(EN['empty']).toBe('Your series remembers nothing yet. It fills up as episodes are approved.');
    expect(VI['narrator']).toBe('Người kể');
    expect(EN['narrator']).toBe('Narrator');
    expect(VI['characters']).toBe('Nhân vật định kỳ');
    expect(EN['characters']).toBe('Recurring characters');
    expect(VI['places']).toBe('Địa điểm');
    expect(EN['places']).toBe('Places');
    expect(VI['rules']).toBe('Quy tắc');
    expect(EN['rules']).toBe('Rules');
    expect(VI['summaries']).toBe('Tóm tắt các tập');
    expect(EN['summaries']).toBe('Past episode summaries');
    expect(VI['saved']).toBe('Đã lưu');
    expect(EN['saved']).toBe('Saved');
    expect(VI['error.conflict']).toBe('Ai đó vừa thay đổi bộ nhớ này. Tải lại rồi lưu lại.');
    expect(EN['error.conflict']).toBe('Someone changed this bible. Reload and save again.');
    expect(VI['error.limits']).toBe('Vượt giới hạn: {{field}}');
    expect(EN['error.limits']).toBe('Over the limit: {{field}}');
    expect(VI['proposal.banner']).toBe('Có {{n}} thay đổi đề xuất từ tập “{{title}}”');
    expect(EN['proposal.banner']).toBe('{{n}} suggested changes from “{{title}}”');
    expect(VI['proposal.accept']).toBe('Chấp nhận');
    expect(EN['proposal.accept']).toBe('Accept');
    expect(VI['proposal.dismiss']).toBe('Bỏ qua');
    expect(EN['proposal.dismiss']).toBe('Dismiss');
    expect(VI['proposal.none']).toBe('Không còn đề xuất nào');
    expect(EN['proposal.none']).toBe('No proposal is pending');
    expect(VI['proposal.autoAccepted']).toBe('Tự động cập nhật (autopilot)');
    expect(EN['proposal.autoAccepted']).toBe('Updated automatically (autopilot)');
  });

  it('ships the keys the task names although no control renders them yet (contract gaps, not defects)', () => {
    // `addSummary` has no add flow (a summary arrives only through a proposal or an approved episode),
    // `autoAccepted` needs a field `BibleDto` does not carry, and `error.ifMatch` is the 428 copy.
    for (const key of ['addSummary', 'proposal.autoAccepted', 'error.ifMatch']) {
      expect(VI[key], `vi ${key}`).toBeTruthy();
      expect(EN[key], `en ${key}`).toBeTruthy();
    }
  });
});
