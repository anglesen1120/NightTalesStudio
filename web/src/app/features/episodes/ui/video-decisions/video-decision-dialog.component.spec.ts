import { TestBed } from '@angular/core/testing';
import { FormControl, FormGroup, Validators } from '@angular/forms';
import type { AbstractControl, ValidationErrors } from '@angular/forms';
import { By } from '@angular/platform-browser';
import { TranslocoTestingModule } from '@jsverse/transloco';
import { afterEach, describe, expect, it } from 'vitest';
import { bindFieldErrors } from '@core/errors';
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

/**
 * A mirror of the store's own `reason` validator (the product's single source of truth, pinned by
 * `video-review.store.spec.ts`): the ui layer may not import `application/` (eslint D2), so the fixture rebuilds the
 * same verdict — trimmed 1–500 and no NUL — for the control the dialog binds.
 */
function rejectReasonValidator(control: AbstractControl<string>): ValidationErrors | null {
  const value = control.value;
  if (value.includes('\u0000')) return { nulCharacter: true };
  const trimmedLength = value.trim().length;
  if (trimmedLength === 0) return { required: true };
  if (trimmedLength > 500) return { maxlength: { requiredLength: 500, actualLength: trimmedLength } };
  return null;
}

async function setup(mode: 'approve' | 'reject' = 'approve', lang: 'vi' | 'en' = 'en') {
  await TestBed.configureTestingModule({ imports: [VideoDecisionDialogComponent, TranslocoTestingModule.forRoot({ langs: { en: { ...coreEn, episodes: episodesEn, errors: errorsEn }, vi: { ...coreVi, episodes: episodesVi, errors: errorsVi }, 'errors/en': errorsEn, 'errors/vi': errorsVi }, translocoConfig: { availableLangs: ['vi', 'en'], defaultLang: lang }, preloadLangs: true })], providers: ZONELESS_TEST_PROVIDERS }).compileComponents();
  const note = new FormControl('', { nonNullable: true, validators: [Validators.maxLength(500)] });
  const reason = new FormControl('', { nonNullable: true, validators: [rejectReasonValidator] });
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
    note.markAsTouched();
    await fixture.whenStable();
    expect(confirmButton(root).disabled).toBe(true);
    expect(root.querySelector('textarea')?.getAttribute('aria-invalid')).toBe('true');
    expect(root.querySelector('nts-char-counter')?.textContent).toContain('501/500');
    expect(root.querySelector('[cost-line]')).toBeNull();
    expect(root.textContent).not.toMatch(/\b(credits?|cost|price|balance)\b|tín dụng|chi phí|số dư/i);
  });

  it('keeps a pristine empty reason unmarked on open and marks it once touched or modified', async () => {
    const { fixture, root, reason } = await setup('reject');
    const textarea = root.querySelector<HTMLTextAreaElement>('[data-testid="reject-reason"]')!;
    // C7: nothing has been typed or submitted, so no red message and no `aria-invalid` — but the confirm stays held
    // back by the invalid reason (the disabled-confirm mechanism is unchanged).
    expect(reason.touched).toBe(false);
    expect(reason.dirty).toBe(false);
    expect(textarea.getAttribute('aria-invalid')).toBeNull();
    expect(textarea.getAttribute('aria-describedby')).not.toContain('-error');
    expect(root.querySelector('.field-error')).toBeNull();
    expect(confirmButton(root).disabled).toBe(true);

    // Typing marks the control dirty, so the message appears without waiting for a blur.
    reason.markAsDirty();
    await fixture.whenStable();
    expect(textarea.getAttribute('aria-invalid')).toBe('true');
    expect(root.querySelector('.field-error')).not.toBeNull();

    // Back to pristine: hidden again — the gate is the interaction, not the invalid value.
    reason.markAsPristine();
    await fixture.whenStable();
    expect(root.querySelector('.field-error')).toBeNull();

    // The store marks the control touched on a failed submit (the native accessor on blur): the message shows again.
    reason.markAsTouched();
    await fixture.whenStable();
    expect(textarea.getAttribute('aria-invalid')).toBe('true');
    expect(textarea.getAttribute('aria-describedby')).toContain(root.querySelector('.field-error')!.id);
  });

  it.each(['', ' ', 'x'.repeat(501)])('refuses an invalid rejection reason %j and links its field error', async (value) => {
    const { fixture, root, reason } = await setup('reject');
    reason.setValue(value);
    reason.markAsTouched();
    await fixture.whenStable();
    const textarea = root.querySelector<HTMLTextAreaElement>('[data-testid="reject-reason"]')!;
    expect(textarea.required).toBe(true);
    expect(textarea.getAttribute('aria-invalid')).toBe('true');
    expect(textarea.getAttribute('aria-describedby')).toContain(root.querySelector('.field-error')!.id);
    expect(confirmButton(root).disabled).toBe(true);
  });

  it('accepts 500 characters plus a trailing newline, judged after trimming like the server', async () => {
    const { fixture, root, reason } = await setup('reject');
    reason.setValue('x'.repeat(500) + '\n');
    reason.markAsTouched();
    await fixture.whenStable();
    // C8/D2: the wire trims before counting, so the client must not block what the server accepts, and the dialog
    // follows the control's own verdict instead of recomputing a raw length. The counter measures the same trimmed
    // value, so a valid 500 is not painted as over-limit.
    expect(reason.invalid).toBe(false);
    expect(root.querySelector('.field-error')).toBeNull();
    expect(root.querySelector<HTMLTextAreaElement>('[data-testid="reject-reason"]')!.getAttribute('aria-invalid')).toBeNull();
    expect(confirmButton(root).disabled).toBe(false);
    const counter = root.querySelector('nts-char-counter')!;
    expect(counter.textContent).toContain('500/500');
    expect(counter.classList.contains('char-counter--over')).toBe(false);
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

  it('renders the catalogue key for a server field error bound by the real binder, never the wire prose', async () => {
    const { fixture, root, reason, note } = await setup('reject');
    reason.setValue('A valid reason');
    // C12: seed the control through the real `bindFieldErrors` so the fixture's `server` error has production's shape
    // (`{ messageKeys: [...] }`, not a bare array), which is what the dialog's `fieldInvalid` must react to.
    const form = new FormGroup({ note, reason });
    bindFieldErrors(
      form,
      { code: 'validation_failed', status: 400, kind: 'validation', fieldErrors: { reason: ['SERVER-DETAIL-PRIVATE'] } },
      { markTouched: true },
    );
    fixture.componentRef.setInput('errorKey', 'errors.validation_failed');
    await fixture.whenStable();
    const textarea = root.querySelector<HTMLTextAreaElement>('[data-testid="reject-reason"]')!;
    expect(textarea.getAttribute('aria-invalid')).toBe('true');
    // The field's own message is the generic catalogue key, verbatim; the binder's `messageKeys` are never rendered.
    expect(root.querySelector('.field-error')?.textContent).toBe(episodesEn.render.review.fieldInvalid);
    // The dialog-level message is the catalogue key of the code it was handed, not the wire's `detail`.
    expect(root.querySelector('[role="alert"]')?.textContent).toBe(errorsEn.validation_failed);
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
