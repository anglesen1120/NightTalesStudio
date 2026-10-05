import { TestBed } from '@angular/core/testing';
import type { MeSummary } from '@core/session';
import { describe, expect, it } from 'vitest';
import { ZONELESS_TEST_PROVIDERS } from '../../testing/providers';
import { onEmit } from '@shared/ui/testing/on-emit';
import { uiTestingTranslations } from '@shared/ui/testing/ui-testing-translations';
import { UserMenuComponent } from './user-menu.component';

const me: MeSummary = {
  id: 'user-1',
  displayName: '<b>Nguyễn An</b>',
  email: 'an@example.test',
  uiLocale: 'vi',
  role: 'owner',
  features: [],
};

async function createUserMenu(summary: MeSummary | null) {
  await TestBed.configureTestingModule({
    imports: [UserMenuComponent, uiTestingTranslations('en')],
    providers: ZONELESS_TEST_PROVIDERS,
  }).compileComponents();
  const fixture = TestBed.createComponent(UserMenuComponent);
  fixture.componentRef.setInput('me', summary);
  fixture.detectChanges();
  await fixture.whenStable();
  return fixture;
}

describe('UserMenuComponent', () => {
  it('renders a safe labelled trigger while the profile is null', async () => {
    const fixture = await createUserMenu(null);
    const trigger = (fixture.nativeElement as HTMLElement).querySelector<HTMLButtonElement>('button');
    expect(trigger?.getAttribute('aria-label')).toBe('Account');
    expect(trigger?.textContent?.trim()).toBe('Account');
  });

  it('shows full name, email and role as plain text and emits logOut once', async () => {
    const fixture = await createUserMenu(me);
    let emissions = 0;
    onEmit(fixture.componentInstance.logOut, () => emissions++);
    const trigger = (fixture.nativeElement as HTMLElement).querySelector<HTMLButtonElement>('button');
    trigger?.click();
    await fixture.whenStable();

    const header = document.querySelector<HTMLElement>('.user-menu__header');
    expect(header?.textContent).toContain('<b>Nguyễn An</b>');
    expect(header?.textContent).toContain('an@example.test');
    expect(header?.textContent).toContain('Owner');
    expect(header?.querySelector('b')).toBeNull();

    document.querySelector<HTMLButtonElement>('button[mat-menu-item]')?.click();
    await fixture.whenStable();
    expect(emissions).toBe(1);
  });

  it('closes on Escape and restores focus to its trigger', async () => {
    const fixture = await createUserMenu(me);
    const trigger = (fixture.nativeElement as HTMLElement).querySelector<HTMLButtonElement>('button');
    trigger?.focus();
    trigger?.click();
    await fixture.whenStable();
    expect(trigger?.getAttribute('aria-expanded')).toBe('true');

    const menu = document.querySelector<HTMLElement>('[role="menu"]');
    expect(menu).not.toBeNull();
    const escape = new KeyboardEvent('keydown', { key: 'Escape', code: 'Escape', bubbles: true });
    Object.defineProperty(escape, 'keyCode', { value: 27 });
    menu?.dispatchEvent(escape);
    await fixture.whenStable();
    expect(trigger?.getAttribute('aria-expanded')).toBe('false');
    expect(document.activeElement).toBe(trigger);
  });

  it('falls back to User for an unknown role and closes when the session clears me', async () => {
    const unknownRole = { ...me, role: 'editor' as MeSummary['role'] };
    const fixture = await createUserMenu(unknownRole);
    const trigger = (fixture.nativeElement as HTMLElement).querySelector<HTMLButtonElement>('button');
    trigger?.click();
    await fixture.whenStable();
    expect(document.querySelector('.user-menu__role')?.textContent?.trim()).toBe('User');

    fixture.componentRef.setInput('me', null);
    await fixture.whenStable();
    expect(trigger?.getAttribute('aria-expanded')).toBe('false');
  });
});
