import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { TranslocoTestingModule, provideTranslocoScope } from '@jsverse/transloco';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { SessionStore } from '@core/session';
import { ZONELESS_TEST_PROVIDERS } from '../../../../testing/providers';
import authEn from '../../../../../assets/i18n/auth/en.json';
import coreEn from '../../../../../assets/i18n/core/en.json';
import errorsEn from '../../../../../assets/i18n/errors/en.json';
import { AuthRepository } from '../../application/auth.repository';
import { FakeAuthRepository } from '../../testing/fake-auth.repository';
import { ResetPasswordPageComponent } from './reset-password-page.component';

const translations = TranslocoTestingModule.forRoot({
  langs: { en: coreEn, 'auth/en': authEn, 'errors/en': errorsEn },
  translocoConfig: { availableLangs: ['en'], defaultLang: 'en', fallbackLang: 'en' },
  preloadLangs: true,
});

const session = { generation: signal(0) };

async function setup(hash: string) {
  window.history.replaceState(null, '', `/reset-password${hash}`);
  const repository = new FakeAuthRepository();
  await TestBed.configureTestingModule({
    imports: [ResetPasswordPageComponent, translations],
    providers: [
      ...ZONELESS_TEST_PROVIDERS,
      provideTranslocoScope('auth'),
      { provide: AuthRepository, useValue: repository },
      { provide: SessionStore, useValue: session },
      provideRouter([]),
    ],
  }).compileComponents();
  const fixture = TestBed.createComponent(ResetPasswordPageComponent);
  fixture.detectChanges();
  await fixture.whenStable();
  return { fixture, repository, root: fixture.nativeElement as HTMLElement };
}

function type(root: HTMLElement, selector: string, value: string): void {
  const input = root.querySelector<HTMLInputElement>(selector);
  if (input === null) throw new Error(`missing ${selector}`);
  input.value = value;
  input.dispatchEvent(new Event('input', { bubbles: true }));
}

afterEach(() => {
  TestBed.resetTestingModule();
  vi.restoreAllMocks();
  window.history.replaceState(null, '', '/');
  session.generation.set(0);
});

describe('ResetPasswordPageComponent', () => {
  it('keeps the form disabled and offers a new link when the reset token is missing', async () => {
    const { fixture, repository, root } = await setup('');
    const form = fixture.componentInstance.form;
    expect(form.disabled).toBe(true);
    expect(root.querySelector('button[type="submit"]:not([disabled])')).toBeNull();
    expect(root.querySelector('a')?.getAttribute('href')).toBe('/login');
    const requestLink = [...root.querySelectorAll<HTMLAnchorElement>('a')].find(
      (link) => link.getAttribute('href') === '/forgot-password',
    );
    expect(requestLink?.textContent).toContain(authEn.reset.requestNew);
    expect(repository.calls).toEqual([]);
  });

  it('announces success through a polite live region and links to login', async () => {
    const { fixture, repository, root } = await setup('#t=abc_123-token');
    expect(window.location.hash).toBe('');
    type(root, '#reset-password', 'a-secure-story-password');
    type(root, '#reset-confirm', 'a-secure-story-password');
    root.querySelector<HTMLFormElement>('form')?.dispatchEvent(new Event('submit', { bubbles: true, cancelable: true }));
    await fixture.whenStable();

    const status = root.querySelector<HTMLElement>('[role="status"][aria-live="polite"]');
    expect(status?.textContent).toContain(authEn.reset.success);
    expect(status?.textContent).toContain(authEn.reset.successHint);
    const login = status?.querySelector<HTMLAnchorElement>('a[href="/login"]');
    expect(login?.textContent).toContain(authEn.reset.goToLogin);
    expect(repository.calls).toEqual([{ method: 'resetPassword', args: ['abc_123-token', 'a-secure-story-password'] }]);
  });

  it('sends exactly one request after a mismatch is corrected by fixing the confirmation', async () => {
    const { fixture, repository, root } = await setup('#t=abc_123-token');
    const submit = async (): Promise<void> => {
      root.querySelector<HTMLFormElement>('form')?.dispatchEvent(new Event('submit', { bubbles: true, cancelable: true }));
      await fixture.whenStable();
      fixture.detectChanges();
    };
    type(root, '#reset-password', 'a-secure-story-password');
    fixture.detectChanges();
    type(root, '#reset-confirm', 'a-secure-story-passwor');
    fixture.detectChanges();
    await submit();
    expect(repository.calls).toEqual([]);
    expect(fixture.componentInstance.form.controls.confirm.errors?.['mismatch']).toBe(true);

    type(root, '#reset-confirm', 'a-secure-story-password');
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();
    expect(fixture.componentInstance.form.controls.confirm.errors?.['mismatch']).toBeUndefined();
    await submit();
    expect(repository.calls).toEqual([{ method: 'resetPassword', args: ['abc_123-token', 'a-secure-story-password'] }]);
  });

  it('sends exactly one request after a mismatch is corrected by fixing the password', async () => {
    const { fixture, repository, root } = await setup('#t=abc_123-token');
    type(root, '#reset-password', 'a-secure-story-passwor');
    fixture.detectChanges();
    type(root, '#reset-confirm', 'a-secure-story-password');
    fixture.detectChanges();
    root.querySelector<HTMLFormElement>('form')?.dispatchEvent(new Event('submit', { bubbles: true, cancelable: true }));
    await fixture.whenStable();
    expect(repository.calls).toEqual([]);
    type(root, '#reset-password', 'a-secure-story-password');
    fixture.detectChanges();
    await fixture.whenStable();
    root.querySelector<HTMLFormElement>('form')?.dispatchEvent(new Event('submit', { bubbles: true, cancelable: true }));
    await fixture.whenStable();
    expect(repository.calls).toHaveLength(1);
  });
});
