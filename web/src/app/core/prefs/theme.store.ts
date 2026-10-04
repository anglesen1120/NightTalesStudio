import { DOCUMENT } from '@angular/common';
import { inject, Injectable, Signal } from '@angular/core';
import { PreferencesStore } from './preferences.store';

export type ThemeName = 'dark' | 'light';

@Injectable({ providedIn: 'root' })
export class ThemeStore {
  private readonly preferences = inject(PreferencesStore);
  private readonly document = inject(DOCUMENT);

  readonly theme: Signal<ThemeName> = this.preferences.theme;

  init(): void {
    this.apply(this.theme());
  }

  setTheme(next: ThemeName): void {
    this.preferences.setTheme(next);
    this.apply(next);
  }

  private apply(theme: ThemeName): void {
    const root = this.document.documentElement;
    if (root.dataset !== undefined) {
      root.dataset['theme'] = theme;
    } else {
      root.setAttribute('data-theme', theme);
    }
  }
}
