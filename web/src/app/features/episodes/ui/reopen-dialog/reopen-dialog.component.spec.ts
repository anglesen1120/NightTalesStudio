import { TestBed } from '@angular/core/testing';
import { TranslocoService, TranslocoTestingModule } from '@jsverse/transloco';
import { afterEach, describe, expect, it } from 'vitest';
import { onEmit } from '@shared/ui/testing/on-emit';
import { ZONELESS_TEST_PROVIDERS } from '../../../../testing/providers';
import coreEn from '../../../../../assets/i18n/core/en.json';
import coreVi from '../../../../../assets/i18n/core/vi.json';
import episodesEn from '../../../../../assets/i18n/episodes/en.json';
import episodesVi from '../../../../../assets/i18n/episodes/vi.json';
import errorsEn from '../../../../../assets/i18n/errors/en.json';
import errorsVi from '../../../../../assets/i18n/errors/vi.json';
import type { AppError } from '@core/errors';
import { ReopenDialogComponent } from './reopen-dialog.component';

async function setup(lang: 'vi' | 'en' = 'vi') {
  await TestBed.configureTestingModule({
    imports: [ReopenDialogComponent, TranslocoTestingModule.forRoot({
      langs: { en: { ...coreEn, episodes: episodesEn, errors: errorsEn }, vi: { ...coreVi, episodes: episodesVi, errors: errorsVi }, 'episodes/en': episodesEn, 'episodes/vi': episodesVi, 'errors/en': errorsEn, 'errors/vi': errorsVi },
      translocoConfig: { availableLangs: ['vi', 'en'], defaultLang: lang, fallbackLang: lang, reRenderOnLangChange: true },
      preloadLangs: true,
    })],
    providers: [...ZONELESS_TEST_PROVIDERS],
  }).compileComponents();
  const fixture = TestBed.createComponent(ReopenDialogComponent);
  await fixture.whenStable();
  return { fixture, component: fixture.componentInstance, root: fixture.nativeElement as HTMLElement };
}

afterEach(() => TestBed.resetTestingModule());

describe('ReopenDialogComponent', () => {
  it.each(['vi', 'en'] as const)('emits reopen and cancellation intents in %s', async (lang) => {
    const { root, component } = await setup(lang);
    let reopened = 0;
    let cancelled = 0;
    onEmit(component.reopened, () => reopened++);
    onEmit(component.cancelled, () => cancelled++);
    root.querySelector<HTMLButtonElement>('.confirm-dialog__actions button:last-child')!.click();
    root.querySelector<HTMLButtonElement>('.confirm-dialog__actions button:first-child')!.click();
    root.querySelector<HTMLButtonElement>('.approval-backdrop')!.click();
    root.querySelector<HTMLElement>('.confirm-dialog')!.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true }));
    expect(reopened).toBe(1);
    expect(cancelled).toBe(3);
  });

  it('ignores all intents while reopening is pending', async () => {
    const { fixture, root, component } = await setup();
    let intents = 0;
    onEmit(component.reopened, () => intents++);
    onEmit(component.cancelled, () => intents++);
    const confirm = root.querySelector<HTMLButtonElement>('.confirm-dialog__actions button:last-child')!;
    confirm.focus();
    fixture.componentRef.setInput('busy', true);
    await fixture.whenStable();
    expect(confirm.disabled).toBe(false);
    expect(confirm.getAttribute('aria-disabled')).toBe('true');
    expect(document.activeElement).toBe(confirm);
    root.querySelectorAll<HTMLButtonElement>('button').forEach((button) => button.click());
    expect(intents).toBe(0);
  });

  it.each(['vi', 'en'] as const)('shows the rate-limit countdown and blocks confirm until the host clears it in %s', async (lang) => {
    const { fixture, root, component } = await setup(lang);
    let reopened = 0;
    onEmit(component.reopened, () => reopened++);
    const confirm = root.querySelector<HTMLButtonElement>('.confirm-dialog__actions button:last-child')!;
    confirm.focus();
    fixture.componentRef.setInput('retryAfterSec', 3);
    await fixture.whenStable();
    expect(root.querySelector('nts-retry-countdown')).not.toBeNull();
    expect(confirm.disabled).toBe(false);
    expect(confirm.getAttribute('aria-disabled')).toBe('true');
    expect(document.activeElement).toBe(confirm);
    confirm.click();
    expect(reopened).toBe(0);
    fixture.componentRef.setInput('retryAfterSec', 0);
    await fixture.whenStable();
    expect(confirm.disabled).toBe(false);
    expect(confirm.getAttribute('aria-disabled')).toBe('true');
    fixture.componentRef.setInput('retryAfterSec', null);
    await fixture.whenStable();
    expect(root.querySelector('nts-retry-countdown')).toBeNull();
    expect(confirm.disabled).toBe(false);
    expect(confirm.getAttribute('aria-disabled')).toBeNull();
    confirm.click();
    expect(reopened).toBe(1);
  });

  it.each([
    ['episode_invalid_state', 409, 'errors.episode_invalid_state'],
    ['not_found', 404, 'errors.not_found'],
    ['internal_error', 500, 'errors.internal_error'],
    ['unexpected_code', 409, 'errors.unknown_error'],
  ] as const)('renders the canonical mapped blocked reason for %s, never raw detail', async (code, status, key) => {
    const { fixture, root } = await setup();
    const error: AppError = { code, status, kind: 'unknown', fieldErrors: {}, problem: { detail: 'DO-NOT-RENDER-DETAIL' } };
    fixture.componentRef.setInput('blocked', error);
    await fixture.whenStable();
    expect(root.querySelector('[role="alert"]')?.textContent?.trim()).toBe(TestBed.inject(TranslocoService).translate(key));
    expect(root.textContent).not.toContain('DO-NOT-RENDER');
  });
});
