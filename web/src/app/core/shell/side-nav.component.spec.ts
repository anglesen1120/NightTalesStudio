import { ChangeDetectionStrategy, Component } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
import { describe, expect, it } from 'vitest';
import { ZONELESS_TEST_PROVIDERS } from '../../testing/providers';
import { onEmit } from '@shared/ui/testing/on-emit';
import { uiTestingTranslations } from '@shared/ui/testing/ui-testing-translations';
import type { NavItem } from './nav-items';
import { SideNavComponent } from './side-nav.component';

@Component({ selector: 'nts-test-icon', template: '<svg aria-hidden="true"></svg>', changeDetection: ChangeDetectionStrategy.OnPush })
class TestIcon {}

@Component({ template: '', changeDetection: ChangeDetectionStrategy.OnPush })
class EmptyPage {}

const items: readonly NavItem[] = [
  { id: 'one', labelKey: 'shell.nav.dashboard', route: '/one', icon: TestIcon, visible: () => true },
  { id: 'two', labelKey: 'shell.nav.series', route: '/two', icon: TestIcon, visible: () => true },
];

async function createSideNav(mode: 'side' | 'over' = 'side') {
  await TestBed.configureTestingModule({
    imports: [SideNavComponent, uiTestingTranslations('en')],
    providers: [
      ...ZONELESS_TEST_PROVIDERS,
      provideRouter([
        { path: 'one', component: EmptyPage },
        { path: 'two', component: EmptyPage },
      ]),
    ],
  }).compileComponents();
  const fixture = TestBed.createComponent(SideNavComponent);
  fixture.componentRef.setInput('items', items);
  fixture.componentRef.setInput('activeRouteId', 'one');
  fixture.componentRef.setInput('mode', mode);
  fixture.detectChanges();
  await TestBed.inject(Router).navigateByUrl('/one');
  await fixture.whenStable();
  return fixture;
}

describe('SideNavComponent', () => {
  it('renders only supplied items and marks the active item', async () => {
    const fixture = await createSideNav();
    const links = (fixture.nativeElement as HTMLElement).querySelectorAll<HTMLAnchorElement>('a[mat-list-item]');
    expect(links).toHaveLength(2);
    expect([...links].map((link) => link.textContent?.trim())).toEqual(['Dashboard', 'Series']);
    expect(links[0]?.getAttribute('aria-current')).toBe('page');
    expect(links[1]?.getAttribute('aria-current')).toBeNull();
  });

  it('renders no placeholder link for an empty item list', async () => {
    const fixture = await createSideNav();
    fixture.componentRef.setInput('items', []);
    await fixture.whenStable();
    expect((fixture.nativeElement as HTMLElement).querySelectorAll('a')).toHaveLength(0);
  });

  it('emits navigation and overlay-close once for click and Enter activation', async () => {
    const fixture = await createSideNav('over');
    let navigations = 0;
    let closes = 0;
    onEmit(fixture.componentInstance.navigate, () => navigations++);
    onEmit(fixture.componentInstance.menuToggle, () => closes++);
    const links = (fixture.nativeElement as HTMLElement).querySelectorAll<HTMLAnchorElement>('a[mat-list-item]');

    links[0]?.click();
    await fixture.whenStable();
    links[1]?.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', bubbles: true }));
    await fixture.whenStable();

    expect(navigations).toBe(2);
    expect(closes).toBe(2);
  });
});
