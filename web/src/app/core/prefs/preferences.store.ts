import { Injectable, signal } from '@angular/core';

export type UiLanguage = 'vi' | 'en';
export type ThemePreference = 'dark' | 'light';

export const PREFERENCES_STORAGE_KEY = 'nts.prefs.v1';

interface StoredPreferences {
  uiLanguage: UiLanguage;
  theme: ThemePreference;
}

const DEFAULTS: StoredPreferences = { uiLanguage: 'vi', theme: 'dark' };

function isUiLanguage(value: unknown): value is UiLanguage {
  return value === 'vi' || value === 'en';
}

function isTheme(value: unknown): value is ThemePreference {
  return value === 'dark' || value === 'light';
}

/**
 * The only localStorage site in the app (README D2/D6): persists the UI language and theme, nothing else.
 */
@Injectable({ providedIn: 'root' })
export class PreferencesStore {
  private readonly languageState = signal<UiLanguage>(DEFAULTS.uiLanguage);
  private readonly themeState = signal<ThemePreference>(DEFAULTS.theme);

  readonly uiLanguage = this.languageState.asReadonly();
  readonly theme = this.themeState.asReadonly();

  private hydrated = false;

  /** Loads the persisted preferences into the signals; falls back to defaults on any problem. */
  read(): StoredPreferences {
    const stored = this.load();
    this.languageState.set(stored.uiLanguage);
    this.themeState.set(stored.theme);
    this.hydrated = true;
    return stored;
  }

  /**
   * Loads stored state once before the first write so an untouched field's default
   * never overwrites a stored value (setTheme before read() must keep stored uiLanguage).
   */
  private ensureHydrated(): void {
    if (!this.hydrated) {
      this.read();
    }
  }

  setLanguage(lang: UiLanguage): void {
    this.ensureHydrated();
    this.languageState.set(lang);
    this.persist();
  }

  setTheme(theme: ThemePreference): void {
    this.ensureHydrated();
    this.themeState.set(theme);
    this.persist();
  }

  private load(): StoredPreferences {
    try {
      if (typeof localStorage === 'undefined') {
        return { ...DEFAULTS };
      }
      const raw = localStorage.getItem(PREFERENCES_STORAGE_KEY);
      if (raw === null) {
        return { ...DEFAULTS };
      }
      const parsed: unknown = JSON.parse(raw);
      if (typeof parsed !== 'object' || parsed === null) {
        return { ...DEFAULTS };
      }
      const record = parsed as Record<string, unknown>;
      return {
        uiLanguage: isUiLanguage(record['uiLanguage']) ? record['uiLanguage'] : DEFAULTS.uiLanguage,
        theme: isTheme(record['theme']) ? record['theme'] : DEFAULTS.theme,
      };
    } catch {
      return { ...DEFAULTS };
    }
  }

  private persist(): void {
    try {
      if (typeof localStorage === 'undefined') {
        return;
      }
      const value: StoredPreferences = { uiLanguage: this.languageState(), theme: this.themeState() };
      localStorage.setItem(PREFERENCES_STORAGE_KEY, JSON.stringify(value));
    } catch {
      // Storage unavailable or full: the in-memory signals remain authoritative.
    }
  }
}
