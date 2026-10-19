import { TestBed } from '@angular/core/testing';
import { TranslocoTestingModule } from '@jsverse/transloco';
import { afterEach, describe, expect, it } from 'vitest';
import { LocaleService } from '@core/i18n/locale.service';
import { ZONELESS_TEST_PROVIDERS } from '../../../../testing/providers';
import episodesEn from '../../../../../assets/i18n/episodes/en.json';
import episodesVi from '../../../../../assets/i18n/episodes/vi.json';
import { FinalVideoMetaComponent } from './final-video-meta.component';

const asset = { assetId: 'asset-1', sizeBytes: 12_345_678, durationSec: 1234.5, retentionUntil: '2026-11-19T12:00:00Z', contentType: 'video/mp4', sha256: 'a'.repeat(64) };
const expiresAt = '2026-10-19T12:15:00Z';

async function setup(lang: 'vi' | 'en') {
  await TestBed.configureTestingModule({ imports: [FinalVideoMetaComponent, TranslocoTestingModule.forRoot({ langs: { en: { episodes: episodesEn }, vi: { episodes: episodesVi } }, translocoConfig: { availableLangs: ['vi', 'en'], defaultLang: lang }, preloadLangs: true })], providers: ZONELESS_TEST_PROVIDERS }).compileComponents();
  TestBed.inject(LocaleService).activate(lang, { persist: false });
  const fixture = TestBed.createComponent(FinalVideoMetaComponent);
  fixture.componentRef.setInput('finalVideo', asset);
  fixture.componentRef.setInput('expiresAt', expiresAt);
  await fixture.whenStable();
  return { fixture, root: fixture.nativeElement as HTMLElement };
}

afterEach(() => TestBed.resetTestingModule());

describe('FinalVideoMetaComponent', () => {
  it.each(['vi', 'en'] as const)('formats size, duration and expiry through the app locale service in %s', async (lang) => {
    const { root } = await setup(lang);
    const tag = lang === 'vi' ? 'vi-VN' : 'en-US';
    const size = new Intl.NumberFormat(tag, { style: 'unit', unit: 'megabyte', maximumFractionDigits: 1 }).format(asset.sizeBytes / 1_000_000);
    const duration = new Intl.NumberFormat(tag, { style: 'unit', unit: 'second', maximumFractionDigits: 1 }).format(asset.durationSec);
    expect([...root.querySelectorAll('dd')].map((item) => item.textContent)).toEqual([size, duration]);
    const time = TestBed.inject(LocaleService).formatDateTime(expiresAt);
    const copy = (lang === 'vi' ? episodesVi : episodesEn).render.review;
    expect(root.querySelector('[data-testid="video-expiry"]')?.textContent).toBe(copy.expiresAt.replace('{{time}}', time));
    expect(root.textContent).toContain(copy.retentionUntil.replace('{{time}}', TestBed.inject(LocaleService).formatDateTime(asset.retentionUntil)));
  });

  it('updates formatting after the language changes without replacing the asset', async () => {
    const { fixture, root } = await setup('vi');
    const before = root.querySelector('dd')?.textContent;
    TestBed.inject(LocaleService).activate('en', { persist: false });
    await fixture.whenStable();
    expect(root.querySelector('dd')?.textContent).not.toBe(before);
    expect(root.querySelector('dd')?.textContent).toBe('12.3 MB');
  });

  it('hides absent metadata and rejects invalid optional timestamps without inventing values', async () => {
    const { fixture, root } = await setup('en');
    fixture.componentRef.setInput('finalVideo', null);
    fixture.componentRef.setInput('expiresAt', null);
    await fixture.whenStable();
    expect(root.querySelector('dl, p')).toBeNull();
    fixture.componentRef.setInput('finalVideo', { ...asset, durationSec: null, retentionUntil: 'invalid' });
    fixture.componentRef.setInput('expiresAt', 'invalid');
    await fixture.whenStable();
    expect(root.querySelectorAll('dd')).toHaveLength(1);
    expect(root.querySelector('p')).toBeNull();
  });
});
