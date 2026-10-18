import { TestBed } from '@angular/core/testing';
import { FormControl, Validators } from '@angular/forms';
import { By } from '@angular/platform-browser';
import { TranslocoTestingModule } from '@jsverse/transloco';
import { afterEach, describe, expect, it } from 'vitest';
import { onEmit } from '@shared/ui/testing/on-emit';
import { RetryCountdown } from '@shared/ui/retry-countdown/retry-countdown.component';
import { ZONELESS_TEST_PROVIDERS } from '../../../../testing/providers';
import coreEn from '../../../../../assets/i18n/core/en.json';
import coreVi from '../../../../../assets/i18n/core/vi.json';
import episodesEn from '../../../../../assets/i18n/episodes/en.json';
import episodesVi from '../../../../../assets/i18n/episodes/vi.json';
import errorsEn from '../../../../../assets/i18n/errors/en.json';
import errorsVi from '../../../../../assets/i18n/errors/vi.json';
import { VideoDecisionDialogComponent } from './video-decision-dialog.component';

async function setup(mode: 'approve' | 'reject' = 'approve', lang: 'vi' | 'en' = 'en') {
  await TestBed.configureTestingModule({ imports: [VideoDecisionDialogComponent, TranslocoTestingModule.forRoot({ langs: { en: { ...coreEn, episodes: episodesEn, errors: errorsEn }, vi: { ...coreVi, episodes: episodesVi, errors: errorsVi }, 'errors/en': errorsEn, 'errors/vi': errorsVi }, translocoConfig: { availableLangs: ['vi', 'en'], defaultLang: lang }, preloadLangs: true })], providers: ZONELESS_TEST_PROVIDERS }).compileComponents();
  const note = new FormControl('', { nonNullable: true, validators: [Validators.maxLength(500)] });
  const reason = new FormControl('', { nonNullable: true, validators: [(control) => Validators.required(control), Validators.maxLength(500)] });
  const fixture = TestBed.createComponent(VideoDecisionDialogComponent);
  fixture.componentRef.setInput('mode', mode);
  fixture.componentRef.setInput('noteControl', note);
  fixture.componentRef.setInput('reasonControl', reason);
  await fixture.whenStable();
  return { fixture, note, reason, root: fixture.nativeElement as HTMLElement };
}
const confirmButton = (root: HTMLElement) => root.querySelector<HTMLButtonElement>('.confirm-dialog__actions button:last-child')!;
afterEach(() => TestBed.resetTestingModule());

describe('VideoDecisionDialogComponent', () => {
  it.each(['vi', 'en'] as const)('renders optional note, body and reactive 500-character counter in %s without a cost line', async (lang) => {
    const { fixture, root, note } = await setup('approve', lang);
    const copy = (lang === 'vi' ? episodesVi : episodesEn).render.review;
    expect(root.querySelector('h2')?.textContent).toBe(copy['approve.title']);
    expect(root.textContent).toContain(copy['approve.body']);
    expect(root.querySelector('textarea')?.required).toBe(false);
    expect(confirmButton(root).disabled).toBe(false);
    note.setValue('x'.repeat(501));
    await fixture.whenStable();
    expect(confirmButton(root).disabled).toBe(true);
    expect(root.querySelector('textarea')?.getAttribute('aria-invalid')).toBe('true');
    expect(root.querySelector('nts-char-counter')?.textContent).toContain('501/500');
    expect(root.querySelector('[cost-line]')).toBeNull();
    expect(root.textContent).not.toMatch(/\b(credits?|cost|price|balance)\b|tín dụng|chi phí|số dư/i);
  });

  it.each(['', ' ', 'x'.repeat(501)])('refuses an invalid rejection reason %j and links its field error', async (value) => {
    const { fixture, root, reason } = await setup('reject');
    reason.setValue(value);
    await fixture.whenStable();
    const textarea = root.querySelector<HTMLTextAreaElement>('[data-testid="reject-reason"]')!;
    expect(textarea.required).toBe(true);
    expect(textarea.getAttribute('aria-invalid')).toBe('true');
    expect(textarea.getAttribute('aria-describedby')).toContain(root.querySelector('.field-error')!.id);
    expect(confirmButton(root).disabled).toBe(true);
  });

  it('keeps exactly two target radios, emits a target intent and uses re-make copy for re_render', async () => {
    const { fixture, root, reason } = await setup('reject');
    expect(root.querySelectorAll('fieldset input[type="radio"]')).toHaveLength(2);
    expect(root.querySelector<HTMLInputElement>('[data-testid="reject-target-story"]')?.checked).toBe(true);
    const targets: string[] = [];
    onEmit(fixture.componentInstance.targetChange, (target) => targets.push(target));
    root.querySelector<HTMLInputElement>('[data-testid="reject-target-re-render"]')!.click();
    expect(targets).toEqual(['re_render']);
    fixture.componentRef.setInput('target', 're_render');
    reason.setValue('Fix the cut');
    await fixture.whenStable();
    expect(root.querySelector('h2')?.textContent).toBe(episodesEn.render.review['reMake.title']);
    expect(root.textContent).toContain(episodesEn.render.review['reMake.body']);
    expect(root.querySelector<HTMLInputElement>('[data-testid="reject-target-re-render"]')?.checked).toBe(true);
    let count = 0;
    onEmit(fixture.componentInstance.confirmed, () => count++);
    confirmButton(root).click();
    expect(count).toBe(1);
  });

  it('maps server field invalidity to aria-describedby without rendering server prose', async () => {
    const { fixture, root, reason } = await setup('reject');
    reason.setValue('A valid reason');
    reason.setErrors({ server: ['SERVER-DETAIL-PRIVATE'] });
    fixture.componentRef.setInput('errorKey', 'errors.validation_failed');
    await fixture.whenStable();
    expect(root.querySelector('textarea')?.getAttribute('aria-invalid')).toBe('true');
    expect(root.querySelector('[role="alert"]')?.getAttribute('aria-live')).toBe('polite');
    expect(root.textContent).not.toContain('SERVER-DETAIL-PRIVATE');
    expect(confirmButton(root).disabled).toBe(true);
  });

  it('holds confirmation during RetryCountdown and busy without removing the focused confirm button', async () => {
    const { fixture, root } = await setup();
    let count = 0;
    onEmit(fixture.componentInstance.confirmed, () => count++);
    fixture.componentRef.setInput('retryAfterSec', 5);
    await fixture.whenStable();
    const confirm = confirmButton(root);
    expect(confirm.disabled).toBe(false);
    expect(confirm.getAttribute('aria-disabled')).toBe('true');
    confirm.click();
    expect(count).toBe(0);
    const countdown = fixture.debugElement.query(By.directive(RetryCountdown)).componentInstance as RetryCountdown;
    countdown.remaining.set(0);
    await fixture.whenStable();
    confirm.click();
    expect(count).toBe(1);
    confirm.focus();
    fixture.componentRef.setInput('busy', true);
    await fixture.whenStable();
    expect(confirm.disabled).toBe(false);
    expect(confirm.getAttribute('aria-disabled')).toBe('true');
    expect(document.activeElement).toBe(confirm);
    confirm.click();
    expect(count).toBe(1);
  });

  it('delegates keyboard focus trap and restore to the static ConfirmDialog', async () => {
    const trigger = document.createElement('button');
    document.body.append(trigger);
    trigger.focus();
    const { fixture, root } = await setup();
    expect(root.querySelector('[role="dialog"]')?.getAttribute('aria-modal')).toBe('true');
    expect(root.querySelectorAll('.cdk-focus-trap-anchor')).toHaveLength(2);
    let dismissed = 0;
    onEmit(fixture.componentInstance.dismissed, () => dismissed++);
    root.querySelector('[role="dialog"]')!.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true }));
    expect(dismissed).toBe(1);
    fixture.destroy();
    expect(document.activeElement).toBe(trigger);
    trigger.remove();
  });
});
