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
});
