// QA (10.5.7-T1) — the 30-row cap on every id-carrying array as the user sees it: the add button is enabled at 29
// rows, disabled at 30 with the limit hint, and a programmatic add at 30 is a no-op. bible-page.qa.spec.ts proves the
// cap for characters only; this file adds places and rules (02.6.3 §3.2: each array caps at 30).
import { DeferBlockBehavior, TestBed } from '@angular/core/testing';
import { ActivatedRoute, convertToParamMap, provideRouter } from '@angular/router';
import { TranslocoTestingModule } from '@jsverse/transloco';
import { of } from 'rxjs';
import { afterEach, describe, expect, it } from 'vitest';
import coreEn from '../../../../assets/i18n/core/en.json';
import coreVi from '../../../../assets/i18n/core/vi.json';
import seriesEn from '../../../../assets/i18n/series/en.json';
import seriesVi from '../../../../assets/i18n/series/vi.json';
import errorsEn from '../../../../assets/i18n/errors/en.json';
import errorsVi from '../../../../assets/i18n/errors/vi.json';
import { ConfirmDialog } from '@shared/ui';
import { ZONELESS_TEST_PROVIDERS } from '../../../testing/providers';
import { BibleRepository } from '../application/bible.repository';
import { BIBLE_SERIES_ID, FakeBibleRepository } from '../testing/fake-bible.repository';
import { BiblePage } from './bible-page';

type Kind = 'characters' | 'places' | 'rules';

const ARRAY_OF: Record<Kind, 'recurringCharacters' | 'places' | 'rules'> = {
  characters: 'recurringCharacters',
  places: 'places',
  rules: 'rules',
};

afterEach(() => TestBed.resetTestingModule());

async function setup() {
  const repo = new FakeBibleRepository();
  const params = convertToParamMap({ id: BIBLE_SERIES_ID });
  await TestBed.configureTestingModule({
    imports: [BiblePage, TranslocoTestingModule.forRoot({ langs: { en: { ...coreEn, series: seriesEn, errors: errorsEn }, vi: { ...coreVi, series: seriesVi, errors: errorsVi }, 'series/en': seriesEn, 'series/vi': seriesVi, 'errors/en': errorsEn, 'errors/vi': errorsVi }, translocoConfig: { availableLangs: ['vi', 'en'], defaultLang: 'en', fallbackLang: 'en', reRenderOnLangChange: true }, preloadLangs: true })],
    providers: [...ZONELESS_TEST_PROVIDERS, provideRouter([]), { provide: BibleRepository, useValue: repo }, { provide: ActivatedRoute, useValue: { paramMap: of(params), snapshot: { paramMap: params } } }, { provide: ConfirmDialog, useValue: { confirm: () => ({ afterClosed: () => of(true) }) } }],
    deferBlockBehavior: DeferBlockBehavior.Manual,
  }).compileComponents();
  const fixture = TestBed.createComponent(BiblePage);
  await fixture.whenStable();
  await expect.poll(() => repo.getCalls.length === 1 && !fixture.componentInstance.store.loading()).toBe(true);
  await fixture.whenStable();
  return { fixture, component: fixture.componentInstance, root: fixture.nativeElement as HTMLElement };
}

function sectionOf(root: HTMLElement, kind: Kind): HTMLElement {
  const section = root.querySelector<HTMLElement>(`nts-bible-section[kind="${kind}"]`);
  if (!section) throw new Error(`missing section ${kind}`);
  return section;
}

function addButtonOf(root: HTMLElement, kind: Kind): HTMLButtonElement {
  const button = sectionOf(root, kind).querySelector<HTMLButtonElement>('button[mat-stroked-button]');
  if (!button) throw new Error(`missing add button in ${kind}`);
  return button;
}

function limitHintOf(root: HTMLElement, kind: Kind): Element | null {
  return sectionOf(root, kind).querySelector('p[role="status"]');
}

describe('BiblePage (QA 30-row cap per array)', () => {
  it.each<Kind>(['characters', 'places', 'rules'])(
    '%s: add is enabled at 29 rows, disabled with the limit hint at 30, and a programmatic add at 30 is a no-op',
    async (kind) => {
      const { fixture, component, root } = await setup();
      const rows = component.form()!.controls[ARRAY_OF[kind]];
      expect(rows.length, `${kind} starting rows`).toBeLessThan(29);

      while (rows.length < 29) component.addEntry(kind);
      fixture.detectChanges();
      await fixture.whenStable();
      expect(addButtonOf(root, kind).disabled, `${kind} add at 29`).toBe(false);
      expect(limitHintOf(root, kind), `${kind} hint at 29`).toBeNull();

      component.addEntry(kind);
      expect(rows.length, `${kind} rows after the 30th add`).toBe(30);
      fixture.detectChanges();
      await fixture.whenStable();
      expect(addButtonOf(root, kind).disabled, `${kind} add at 30`).toBe(true);
      expect(limitHintOf(root, kind), `${kind} hint at 30`).not.toBeNull();

      component.addEntry(kind);
      expect(rows.length, `${kind} programmatic add at 30`).toBe(30);
    },
  );
});
