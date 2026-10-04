import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { TranslocoService } from '@jsverse/transloco';
import { PreferencesStore } from '@core/prefs';
import { ZONELESS_TEST_PROVIDERS } from '../../testing/providers';
import { LocaleService } from './locale.service';
import type { AppLocale } from './locale.model';
import { provideAppI18n } from './transloco.config';

const INSTANT = Date.UTC(2026, 9, 4, 14, 30); // 2026-10-04T14:30Z = 21:30 in Asia/Ho_Chi_Minh
const DATE_UTC: Intl.DateTimeFormatOptions = { day: '2-digit', month: '2-digit', year: 'numeric', timeZone: 'UTC' };

function setup(): LocaleService {
  TestBed.configureTestingModule({
    providers: [...ZONELESS_TEST_PROVIDERS, provideHttpClient(), provideHttpClientTesting(), provideAppI18n()],
  });
  return TestBed.inject(LocaleService);
}

describe('LocaleService', () => {
  beforeEach(() => {
    document.documentElement.lang = '';
  });

  describe('resolveInitial (profile > pref > browser > vi)', () => {
    const rows: readonly [string, unknown, unknown, readonly string[], AppLocale][] = [
      ['profile wins over pref and browser', 'en', 'vi', ['vi-VN'], 'en'],
      ['profile vi wins over an en pref', 'vi', 'en', ['en-US'], 'vi'],
      ['pref wins when there is no profile', undefined, 'en', ['vi-VN'], 'en'],
      ['pref wins when the profile is null', null, 'vi', ['en-GB'], 'vi'],
      ['invalid pref is skipped, browser decides', undefined, 'fr', ['en-GB'], 'en'],
      ['invalid profile is skipped', 'de', 'en', [], 'en'],
      ['browser vi-VN, en', undefined, undefined, ['vi-VN', 'en'], 'vi'],
      ['browser en-GB', undefined, undefined, ['en-GB'], 'en'],
      ['first supported browser tag wins over an unsupported one', undefined, undefined, ['fr-FR', 'en-US'], 'en'],
      ['unsupported browser tag falls back to vi', undefined, undefined, ['fr-FR'], 'vi'],
      ['no browser languages falls back to vi', undefined, undefined, [], 'vi'],
      ['browser tag matching is case-insensitive on the primary subtag', undefined, undefined, ['EN-us'], 'en'],
    ];

    for (const [name, profile, pref, browser, expected] of rows) {
      it(name, () => {
        expect(setup().resolveInitial(profile, pref, browser)).toBe(expected);
      });
    }
  });

  describe('activate', () => {
    it('sets Transloco, <html lang>, the signal and the saved preference', () => {
      const locale = setup();
      const transloco = TestBed.inject(TranslocoService);
      const prefs = TestBed.inject(PreferencesStore);

      locale.activate('en', { persist: true });

      expect(transloco.getActiveLang()).toBe('en');
      expect(document.documentElement.lang).toBe('en');
      expect(prefs.savedUiLanguage()).toBe('en');
    });

    it('follows the language back and forth', () => {
      const locale = setup();
      locale.activate('en', { persist: false });
      locale.activate('vi', { persist: false });
      expect(document.documentElement.lang).toBe('vi');
      expect(TestBed.inject(TranslocoService).getActiveLang()).toBe('vi');
      expect(locale.locale()).toBe('vi');
    });

    it('switches everything local with persist:false but saves no choice', () => {
      const locale = setup();
      locale.activate('en', { persist: false });
      expect(TestBed.inject(PreferencesStore).savedUiLanguage()).toBeNull();
      expect(document.documentElement.lang).toBe('en');
      expect(TestBed.inject(TranslocoService).getActiveLang()).toBe('en');
    });

    it('keeps the local locale when persistence fails (storage unavailable)', () => {
      const locale = setup();
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
  });

  describe('formats', () => {
    it('renders a fixed instant differently for vi-VN and en-US', () => {
      const locale = setup();
      locale.activate('vi', { persist: false });
      expect(locale.formatDate(INSTANT, DATE_UTC)).toBe('04/10/2026');
      locale.activate('en', { persist: false });
      expect(locale.formatDate(INSTANT, DATE_UTC)).toBe('10/04/2026');
    });

    it('renders a fixed number differently for vi-VN and en-US', () => {
      const locale = setup();
      locale.activate('vi', { persist: false });
      expect(locale.formatNumber(1234567.891)).toBe('1.234.567,891');
      locale.activate('en', { persist: false });
      expect(locale.formatNumber(1234567.891)).toBe('1,234,567.891');
    });

    it('formatDateTime uses the caller options', () => {
      const locale = setup();
      locale.activate('en', { persist: false });
      expect(locale.formatDateTime(INSTANT, { hour: '2-digit', minute: '2-digit', hourCycle: 'h23', timeZone: 'UTC' })).toBe(
        '14:30',
      );
    });

    it('formatInZone shows the wall-clock time in the zone and the zone id', () => {
      const locale = setup();
      locale.activate('vi', { persist: false });
      const vi = locale.formatInZone(INSTANT, 'Asia/Ho_Chi_Minh');
      expect(vi).toContain('21:30');
      expect(vi).toContain('04/10/2026');
      expect(vi.endsWith(' Asia/Ho_Chi_Minh')).toBe(true);

      locale.activate('en', { persist: false });
      const en = locale.formatInZone(INSTANT, 'Asia/Ho_Chi_Minh');
      expect(en).toContain('21:30');
      expect(en).toContain('10/04/2026');
      expect(en.endsWith(' Asia/Ho_Chi_Minh')).toBe(true);
      expect(locale.formatInZone(INSTANT, 'UTC')).toContain('14:30');
    });

    it('formatRelative renders past and future in both languages', () => {
      const locale = setup();
      const now = INSTANT;
      locale.activate('en', { persist: false });
      expect(locale.formatRelative(now - 2 * 3_600_000, { now })).toBe('2 hours ago');
      expect(locale.formatRelative(now + 3 * 86_400_000, { now })).toBe('in 3 days');
      expect(locale.formatRelative(now + 86_400_000, { now })).toBe('tomorrow');
      locale.activate('vi', { persist: false });
      expect(locale.formatRelative(now - 2 * 3_600_000, { now })).toContain('giờ');
      expect(locale.formatRelative(now + 3 * 86_400_000, { now })).toContain('ngày');
    });

    it('accepts Date, number and ISO string instants alike', () => {
      const locale = setup();
      locale.activate('en', { persist: false });
      const iso = new Date(INSTANT).toISOString();
      expect(locale.formatDate(iso, DATE_UTC)).toBe(locale.formatDate(new Date(INSTANT), DATE_UTC));
      expect(locale.formatDate(INSTANT, DATE_UTC)).toBe(locale.formatDate(iso, DATE_UTC));
    });
  });
});
