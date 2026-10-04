import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { TranslocoService } from '@jsverse/transloco';
import coreEn from '../../../assets/i18n/core/en.json';
import coreVi from '../../../assets/i18n/core/vi.json';
import { ZONELESS_TEST_PROVIDERS } from '../../testing/providers';
import { LocaleService } from './locale.service';
import { MissingKeyHandler } from './missing-key.handler';
import { provideAppI18n } from './transloco.config';

// QA (10.2.5-T1, acceptance "a key truly absent everywhere renders the key path, never empty/undefined, dev logs it
// once per key"; "a key missing from en is not shown as the vi value").
describe('MissingKeyHandler', () => {
  let warnings: unknown[][];
  let originalWarn: typeof console.warn;

  beforeEach(() => {
    warnings = [];
    originalWarn = console.warn;
    console.warn = (...args: unknown[]) => {
      warnings.push(args);
    };
  });

  afterEach(() => {
    console.warn = originalWarn;
  });

  function setup(): { http: HttpTestingController; transloco: TranslocoService; locale: LocaleService } {
    TestBed.configureTestingModule({
      providers: [...ZONELESS_TEST_PROVIDERS, provideHttpClient(), provideHttpClientTesting(), provideAppI18n()],
    });
    return {
      http: TestBed.inject(HttpTestingController),
      transloco: TestBed.inject(TranslocoService),
      locale: TestBed.inject(LocaleService),
    };
  }

  it('returns the key path for a key absent from the active language, never an empty string', () => {
    const handler = new MissingKeyHandler();
    const data = { activeLang: 'vi', translation: {}, config: {} } as never;
    expect(handler.handle('a.b.c', data)).toBe('a.b.c');
    expect(handler.handle('x', data)).toBe('x');
  });

  it('warns once per key and language in development, and separately for the other language', () => {
    const handler = new MissingKeyHandler();
    const vi = { activeLang: 'vi', translation: {}, config: {} } as never;
    const en = { activeLang: 'en', translation: {}, config: {} } as never;
    handler.handle('a.b', vi);
    handler.handle('a.b', vi);
    handler.handle('a.b', en);
    handler.handle('other', vi);
    expect(warnings).toEqual([
      ['missing key', 'a.b', 'vi'],
      ['missing key', 'a.b', 'en'],
      ['missing key', 'other', 'vi'],
    ]);
  });

  it('renders the key path through Transloco for a key absent from both files, in both languages', async () => {
    const { http, transloco, locale } = setup();
    locale.activate('vi', { persist: false });
    const loaded = new Promise<void>((resolve) => transloco.load('vi').subscribe({ complete: resolve }));
    http.expectOne('/assets/i18n/core/vi.json').flush(coreVi);
    await loaded;
    expect(transloco.translate('qa.notDefinedAnywhere')).toBe('qa.notDefinedAnywhere');
    expect(transloco.translate('common.close')).toBe('Đóng');

    locale.activate('en', { persist: false });
    const loadedEn = new Promise<void>((resolve) => transloco.load('en').subscribe({ complete: resolve }));
    http.expectOne('/assets/i18n/core/en.json').flush(coreEn);
    await loadedEn;
    expect(transloco.translate('qa.notDefinedAnywhere')).toBe('qa.notDefinedAnywhere');
  });

  it('does not show the vi text when the key is missing from the en file (no hidden language fallback)', async () => {
    const { http, transloco, locale } = setup();
    // vi is loaded first and has `common.close`; en is served without it.
    const viLoaded = new Promise<void>((resolve) => transloco.load('vi').subscribe({ complete: resolve }));
    http.expectOne('/assets/i18n/core/vi.json').flush(coreVi);
    await viLoaded;

    locale.activate('en', { persist: false });
    const enLoaded = new Promise<void>((resolve) => transloco.load('en').subscribe({ complete: resolve }));
    const commonWithoutClose: Record<string, string> = { ...coreEn.common };
    delete commonWithoutClose['close'];
    http.expectOne('/assets/i18n/core/en.json').flush({ ...coreEn, common: commonWithoutClose });
    await enLoaded;

    expect(transloco.translate('common.close')).toBe('common.close');
    http.verify();
  });
});
