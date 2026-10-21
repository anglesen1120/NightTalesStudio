import { Injectable, computed, signal } from '@angular/core';

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

  private readonly languageSavedState = signal(false);
  /** The language the user saved (stored value or `setLanguage`), `null` while only the default applies. */
  readonly savedUiLanguage = computed(() => (this.languageSavedState() ? this.languageState() : null));

  private hydrated = false;

  /** Loads the persisted preferences into the signals; falls back to defaults on any problem. */
  read(): StoredPreferences {
    const { stored, languageSaved } = this.load();
    this.languageState.set(stored.uiLanguage);
    this.themeState.set(stored.theme);
    this.languageSavedState.set(languageSaved);
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
    this.languageSavedState.set(true);
    this.persist();
  }

  setTheme(theme: ThemePreference): void {
    this.ensureHydrated();
    this.themeState.set(theme);
    this.persist();
  }

  private load(): { stored: StoredPreferences; languageSaved: boolean } {
    const defaults = { stored: { ...DEFAULTS }, languageSaved: false };
    try {
      if (typeof localStorage === 'undefined') {
        return defaults;
      }
      const raw = localStorage.getItem(PREFERENCES_STORAGE_KEY);
      if (raw === null) {
        return defaults;
      }
      const parsed: unknown = JSON.parse(raw);
      if (typeof parsed !== 'object' || parsed === null) {
        return defaults;
      }
      const record = parsed as Record<string, unknown>;
      const languageSaved = isUiLanguage(record['uiLanguage']);
      return {
        stored: {
          uiLanguage: isUiLanguage(record['uiLanguage']) ? record['uiLanguage'] : DEFAULTS.uiLanguage,
          theme: isTheme(record['theme']) ? record['theme'] : DEFAULTS.theme,
        },
        languageSaved,
      };
    } catch {
      return defaults;
    }
  }

  private persist(): void {
    try {
      if (typeof localStorage === 'undefined') {
        return;
      }
      // The default language is never written, so "no saved choice" stays distinguishable (README D10 order).
      const value: Partial<StoredPreferences> = {
        ...(this.languageSavedState() ? { uiLanguage: this.languageState() } : {}),
        theme: this.themeState(),
      };
      localStorage.setItem(PREFERENCES_STORAGE_KEY, JSON.stringify(value));
    } catch {
      // Storage unavailable or full: the in-memory signals remain authoritative.
    }
  }
}
