import { DOCUMENT } from '@angular/common';
import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { describe, expect, it, vi } from 'vitest';
import { PreferencesStore } from './preferences.store';
import { ThemeStore, type ThemeName } from './theme.store';

class FakePreferencesStore {
  private readonly state = signal<ThemeName>('dark');
  readonly theme = this.state.asReadonly();
  readonly setTheme = vi.fn((theme: ThemeName) => this.state.set(theme));
}

describe('ThemeStore', () => {
  function setup(initial: ThemeName = 'dark') {
    const preferences = new FakePreferencesStore();
    preferences.setTheme(initial);
    preferences.setTheme.mockClear();
    TestBed.configureTestingModule({ providers: [{ provide: PreferencesStore, useValue: preferences }] });
    return { store: TestBed.inject(ThemeStore), preferences, document: TestBed.inject(DOCUMENT) };
  }

  it('applies dark by default during init', () => {
    const { store, document } = setup();
    store.init();
    expect(store.theme()).toBe('dark');
    expect(document.documentElement.dataset['theme']).toBe('dark');
  });

  it('persists and applies light through PreferencesStore only', () => {
    const { store, preferences, document } = setup();
    store.setTheme('light');
    expect(preferences.setTheme).toHaveBeenCalledWith('light');
    expect(document.documentElement.dataset['theme']).toBe('light');
  });

  it('restores the validated preference on reload', () => {
    const { store, document } = setup('light');
    store.init();
    expect(store.theme()).toBe('light');
    expect(document.documentElement.dataset['theme']).toBe('light');
  });
});
