import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { PREFERENCES_STORAGE_KEY, PreferencesStore } from './preferences.store';

describe('PreferencesStore', () => {
  beforeEach(() => {
    localStorage.clear();
    TestBed.configureTestingModule({ providers: [provideZonelessChangeDetection()] });
  });

  afterEach(() => {
    vi.unstubAllGlobals();
    localStorage.clear();
  });

  it('defaults to vi/dark when the key is absent', () => {
    const store = TestBed.inject(PreferencesStore);
    expect(store.read()).toEqual({ uiLanguage: 'vi', theme: 'dark' });
    expect(store.uiLanguage()).toBe('vi');
    expect(store.theme()).toBe('dark');
  });

  it('round-trips setLanguage/setTheme through read()', () => {
    const first = TestBed.inject(PreferencesStore);
    first.setLanguage('en');
    first.setTheme('light');

    TestBed.resetTestingModule();
    TestBed.configureTestingModule({ providers: [provideZonelessChangeDetection()] });
    const second = TestBed.inject(PreferencesStore);

    expect(second.read()).toEqual({ uiLanguage: 'en', theme: 'light' });
    expect(second.uiLanguage()).toBe('en');
    expect(second.theme()).toBe('light');
  });

  it('falls back to defaults on corrupt JSON without throwing', () => {
    localStorage.setItem(PREFERENCES_STORAGE_KEY, '{not json');
    const store = TestBed.inject(PreferencesStore);
    expect(() => store.read()).not.toThrow();
    expect(store.uiLanguage()).toBe('vi');
    expect(store.theme()).toBe('dark');
  });

  it('falls back to defaults for unknown values', () => {
    localStorage.setItem(PREFERENCES_STORAGE_KEY, JSON.stringify({ uiLanguage: 'fr', theme: 'blue' }));
    const store = TestBed.inject(PreferencesStore);
    expect(store.read()).toEqual({ uiLanguage: 'vi', theme: 'dark' });
  });

  it('does not throw when localStorage is unavailable', () => {
    vi.stubGlobal('localStorage', undefined);
    const store = TestBed.inject(PreferencesStore);
    expect(() => store.read()).not.toThrow();
    expect(() => store.setLanguage('en')).not.toThrow();
    expect(() => store.setTheme('light')).not.toThrow();
    expect(store.uiLanguage()).toBe('en');
    expect(store.theme()).toBe('light');
  });

  it('writes no key other than nts.prefs.v1', () => {
    const store = TestBed.inject(PreferencesStore);
    store.setLanguage('en');
    store.setTheme('light');
    expect(localStorage.length).toBe(1);
    expect(localStorage.key(0)).toBe(PREFERENCES_STORAGE_KEY);
    expect(JSON.parse(localStorage.getItem(PREFERENCES_STORAGE_KEY) ?? '')).toEqual({
      uiLanguage: 'en',
      theme: 'light',
    });
  });

  it('falls back to defaults when the stored JSON is not an object', () => {
    for (const raw of ['null', '42', '"vi"', 'true', '[]']) {
      localStorage.setItem(PREFERENCES_STORAGE_KEY, raw);
      TestBed.resetTestingModule();
      TestBed.configureTestingModule({ providers: [provideZonelessChangeDetection()] });
      const store = TestBed.inject(PreferencesStore);
      expect(store.read(), raw).toEqual({ uiLanguage: 'vi', theme: 'dark' });
    }
  });

  it('keeps a valid field when the other one is invalid or of the wrong type', () => {
    localStorage.setItem(PREFERENCES_STORAGE_KEY, JSON.stringify({ uiLanguage: 'en', theme: 'blue' }));
    expect(TestBed.inject(PreferencesStore).read()).toEqual({ uiLanguage: 'en', theme: 'dark' });

    localStorage.setItem(PREFERENCES_STORAGE_KEY, JSON.stringify({ uiLanguage: 1, theme: 'light' }));
    expect(TestBed.inject(PreferencesStore).read()).toEqual({ uiLanguage: 'vi', theme: 'light' });

    localStorage.setItem(PREFERENCES_STORAGE_KEY, JSON.stringify({ theme: 'light' }));
    expect(TestBed.inject(PreferencesStore).read()).toEqual({ uiLanguage: 'vi', theme: 'light' });
  });

  it('treats stored values as case-sensitive', () => {
    localStorage.setItem(PREFERENCES_STORAGE_KEY, JSON.stringify({ uiLanguage: 'EN', theme: 'Light' }));
    expect(TestBed.inject(PreferencesStore).read()).toEqual({ uiLanguage: 'vi', theme: 'dark' });
  });

  it('does not throw and falls back to defaults when getItem throws', () => {
    vi.stubGlobal('localStorage', {
      getItem: () => {
        throw new Error('SecurityError');
      },
      setItem: () => undefined,
    });
    const store = TestBed.inject(PreferencesStore);
    expect(() => store.read()).not.toThrow();
    expect(store.uiLanguage()).toBe('vi');
    expect(store.theme()).toBe('dark');
  });

  it('does not throw and keeps the in-memory value when setItem throws (quota or private mode)', () => {
    vi.stubGlobal('localStorage', {
      getItem: () => null,
      setItem: () => {
        throw new Error('QuotaExceededError');
      },
    });
    const store = TestBed.inject(PreferencesStore);
    expect(() => store.setLanguage('en')).not.toThrow();
    expect(() => store.setTheme('light')).not.toThrow();
    expect(store.uiLanguage()).toBe('en');
    expect(store.theme()).toBe('light');
  });

  it('changing one preference keeps the other persisted value', () => {
    const store = TestBed.inject(PreferencesStore);
    store.setLanguage('en');
    expect(JSON.parse(localStorage.getItem(PREFERENCES_STORAGE_KEY) ?? '')).toEqual({
      uiLanguage: 'en',
      theme: 'dark',
    });
    store.setTheme('light');
    expect(JSON.parse(localStorage.getItem(PREFERENCES_STORAGE_KEY) ?? '')).toEqual({
      uiLanguage: 'en',
      theme: 'light',
    });
  });

  it('persists only uiLanguage and theme, dropping unknown stored fields (README D6)', () => {
    localStorage.setItem(
      PREFERENCES_STORAGE_KEY,
      JSON.stringify({ uiLanguage: 'en', theme: 'light', token: 'secret', extra: 1 }),
    );
    const store = TestBed.inject(PreferencesStore);
    store.read();
    store.setTheme('dark');
    expect(Object.keys(JSON.parse(localStorage.getItem(PREFERENCES_STORAGE_KEY) ?? '')).sort()).toEqual([
      'theme',
      'uiLanguage',
    ]);
  });
});
