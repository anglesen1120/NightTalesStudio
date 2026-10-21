import { BreakpointObserver, type BreakpointState } from '@angular/cdk/layout';
import { ChangeDetectionStrategy, Component, DestroyRef, TemplateRef, signal, viewChild } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { By } from '@angular/platform-browser';
import { provideRouter } from '@angular/router';
import type { AppLocale } from '@core/i18n';
import type { RealtimeState } from '@core/realtime';
import type { MeSummary } from '@core/session';
import { of } from 'rxjs';
import { describe, expect, it } from 'vitest';
import { ZONELESS_TEST_PROVIDERS } from '../../testing/providers';
import { uiTestingTranslations } from '@shared/ui/testing/ui-testing-translations';
import { HeaderEndRegistry } from './header-end.registry';
import type { NavItem } from './nav-items';
import { ShellComponent } from './shell.component';
import { ShellStore } from './shell.store';

class FakeShellStore {
  readonly summary = signal<MeSummary | null>(null);
  readonly navItems = signal<readonly NavItem[]>([]);
  readonly realtimeState = signal<RealtimeState>('connected');
  readonly pageTitle = signal('Dashboard');
  readonly locale = signal<AppLocale>('en');
  readonly profileError = signal<object | null>(null);
  readonly localeSaving = signal(false);
  initCount = 0;
  retryCount = 0;

  init(): void {
    this.initCount++;
  }

  loadMe(): Promise<void> { return Promise.resolve(); }

  retryProfile(): Promise<void> {
    this.retryCount++;
    return Promise.resolve();
  }

  changeLocale(locale: AppLocale): Promise<void> {
    this.locale.set(locale);
    return Promise.resolve();
  }

  logOut(): void {}

  resumeRealtime(): void {}
}

class ManualDestroyRef implements DestroyRef {
  private readonly callbacks = new Set<() => void>();
  destroyed = false;

  onDestroy(callback: () => void): () => void {
    this.callbacks.add(callback);
    return () => this.callbacks.delete(callback);
  }

  destroy(): void {
    this.destroyed = true;
    for (const callback of this.callbacks) callback();
    this.callbacks.clear();
  }
}

@Component({ selector: 'nts-shell-test-icon', template: '', changeDetection: ChangeDetectionStrategy.OnPush })
class TestIcon {}

@Component({ template: '', changeDetection: ChangeDetectionStrategy.OnPush })
class EmptyPage {}

@Component({
  imports: [ShellComponent],
  template: `
    <ng-template #slotA><button id="header-action-a" type="button">Header action A</button></ng-template>
    <ng-template #slotB><button id="header-action-b" type="button">Header action B</button></ng-template>
    <nts-shell />
  `,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
class ShellHost {
  readonly slotA = viewChild.required<TemplateRef<unknown>>('slotA');
  readonly slotB = viewChild.required<TemplateRef<unknown>>('slotB');
}

async function createShell(mobile = false) {
  const store = new FakeShellStore();
  await TestBed.configureTestingModule({
    imports: [ShellHost, uiTestingTranslations('en')],
    providers: [
      ...ZONELESS_TEST_PROVIDERS,
      provideRouter([{ path: 'one', component: EmptyPage }]),
      {
        provide: BreakpointObserver,
        useValue: {
          observe: () => of<BreakpointState>({ matches: mobile, breakpoints: { '(max-width: 599px)': mobile } }),
        },
      },
    ],
  })
    .overrideComponent(ShellComponent, {
      set: {
        providers: [
          { provide: ShellStore, useValue: store },
          HeaderEndRegistry,
        ],
      },
    })
    .compileComponents();
  const fixture = TestBed.createComponent(ShellHost);
  fixture.detectChanges();
  await fixture.whenStable();
  const registry = fixture.debugElement.query(By.directive(ShellComponent)).injector.get(HeaderEndRegistry);
  return { fixture, registry, store };
}

describe('ShellComponent', () => {
  it('initializes once and exposes skip-link, header, navigation and main landmarks', async () => {
    const { fixture, store } = await createShell();
    const root = fixture.nativeElement as HTMLElement;
    const skip = root.querySelector<HTMLAnchorElement>('.shell__skip-link');
    const main = root.querySelector<HTMLElement>('main#content');

    expect(store.initCount).toBe(1);
    expect(root.firstElementChild?.querySelector('.shell__skip-link') ?? root.firstElementChild).toBeTruthy();
    expect(root.querySelector('header')).not.toBeNull();
    expect(root.querySelector('mat-nav-list[aria-label="Main navigation"]')).not.toBeNull();
    expect(main?.getAttribute('tabindex')).toBe('-1');

    skip?.click();
    await fixture.whenStable();
    expect(document.activeElement).toBe(main);
  });

  it('renders the current header-end template and ignores a stale disposer', async () => {
    const { fixture, registry } = await createShell();
    const root = fixture.nativeElement as HTMLElement;
    const ownerA = new ManualDestroyRef();
    const ownerB = new ManualDestroyRef();
    const disposeA = registry.register(fixture.componentInstance.slotA(), ownerA);
    await fixture.whenStable();
    expect(root.querySelector('#header-action-a')?.textContent).toContain('Header action A');

    registry.register(fixture.componentInstance.slotB(), ownerB);
    await fixture.whenStable();
    expect(root.querySelector('#header-action-a')).toBeNull();
    expect(root.querySelector('#header-action-b')?.textContent).toContain('Header action B');

    disposeA();
    await fixture.whenStable();
    expect(root.querySelector('#header-action-b')).not.toBeNull();

    ownerB.destroy();
    await fixture.whenStable();
    expect(root.querySelector('#header-action-b')).toBeNull();
  });

  it('keeps the outlet usable while profile failure offers retry', async () => {
    const { fixture, store } = await createShell();
    store.profileError.set({});
    await fixture.whenStable();
    const root = fixture.nativeElement as HTMLElement;
    expect(root.querySelector('router-outlet')).not.toBeNull();
    expect(root.textContent).toContain('Could not load your account');

    root.querySelector<HTMLButtonElement>('nts-error-state button')?.click();
    await fixture.whenStable();
    expect(store.retryCount).toBe(1);
  });

  it('makes the background inert and restores hamburger focus when the mobile drawer closes', async () => {
    const { fixture, store } = await createShell(true);
    store.navItems.set([
      { id: 'one', labelKey: 'shell.nav.dashboard', route: '/one', icon: TestIcon, visible: () => true },
    ]);
    await fixture.whenStable();
    const root = fixture.nativeElement as HTMLElement;
    const trigger = root.querySelector<HTMLButtonElement>('.top-bar__menu');
    trigger?.click();
    await fixture.whenStable();

    expect(root.querySelector('.shell__workspace')?.hasAttribute('inert')).toBe(true);
    expect(document.activeElement).toBe(root.querySelector('a[mat-list-item]'));

    document.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true }));
    await fixture.whenStable();
    expect(root.querySelector('.shell__workspace')?.hasAttribute('inert')).toBe(false);
    expect(document.activeElement).toBe(trigger);
  });
});
