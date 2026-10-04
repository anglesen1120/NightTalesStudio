import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { LocaleService, provideAppI18n } from '@core/i18n';
import { ZONELESS_TEST_PROVIDERS } from '../../testing/providers';
import { PREFERENCES_STORAGE_KEY } from './preferences.store';

// Lives in core/prefs because browser storage may only be touched here (README D2/D6): it proves that
// `LocaleService.activate` writes the language preference through the prefs store and nothing else.
describe('LocaleService persistence', () => {
  beforeEach(() => {
    localStorage.clear();
    TestBed.configureTestingModule({
      providers: [...ZONELESS_TEST_PROVIDERS, provideHttpClient(), provideHttpClientTesting(), provideAppI18n()],
    });
  });

  afterEach(() => localStorage.clear());

  it('activate(..., { persist: true }) writes exactly one key, nts.prefs.v1, holding the language', () => {
    TestBed.inject(LocaleService).activate('en', { persist: true });
    expect(localStorage.length).toBe(1);
    expect(localStorage.key(0)).toBe(PREFERENCES_STORAGE_KEY);
    expect(JSON.parse(localStorage.getItem(PREFERENCES_STORAGE_KEY) ?? '')).toEqual({ uiLanguage: 'en', theme: 'dark' });
  });

  it('activate(..., { persist: false }) writes nothing', () => {
    const locale = TestBed.inject(LocaleService);
    locale.activate('en', { persist: false });
    locale.activate('vi', { persist: false });
    expect(localStorage.length).toBe(0);
  });

  it('keeps the local locale when persistence fails (storage unavailable)', () => {
    const locale = TestBed.inject(LocaleService);
    vi.stubGlobal('localStorage', {
      getItem: () => null,
      setItem: () => {
        throw new Error('quota');
      },
    });
    try {
      locale.activate('en', { persist: true });
      expect(locale.locale()).toBe('en');
      expect(document.documentElement.lang).toBe('en');
    } finally {
      vi.unstubAllGlobals();
    }
  });

  it('formatting never writes', () => {
    const locale = TestBed.inject(LocaleService);
    locale.formatDate(Date.now());
    locale.formatNumber(1);
    locale.formatRelative(Date.now());
    locale.formatInZone(Date.now(), 'UTC');
    expect(localStorage.length).toBe(0);
  });
});
