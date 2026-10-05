import { DOCUMENT } from '@angular/common';
import { ChangeDetectionStrategy, Component, inject } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { MatDialog, MatDialogModule, type MatDialogRef } from '@angular/material/dialog';
import { NoopAnimationsModule } from '@angular/platform-browser/animations';
import { TranslocoTestingModule } from '@jsverse/transloco';
import { describe, expect, it, vi } from 'vitest';
import errorsEn from '../../../../../assets/i18n/errors/en.json';
import reauthEn from '../../../../../assets/i18n/reauth/en.json';
import reauthVi from '../../../../../assets/i18n/reauth/vi.json';
import { ZONELESS_TEST_PROVIDERS } from '../../../../testing/providers';
import { onEmit } from '../../testing/on-emit';
import { ReauthDialogComponent } from './reauth-dialog.component';
import {
  REAUTH_DIALOG_IDLE,
  type ReauthDialogData,
  type ReauthDialogResult,
  type ReauthDialogState,
} from './reauth-dialog.model';

const translations = TranslocoTestingModule.forRoot({
  langs: { en: {}, 'reauth/en': reauthEn, 'reauth/vi': reauthVi, 'errors/en': errorsEn },
  translocoConfig: { availableLangs: ['en'], defaultLang: 'en', fallbackLang: 'en' },
});

@Component({
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `<button id="trigger" type="button" (click)="open({ requireCode: true })">open</button>`,
})
class DialogHost {
  private readonly dialog = inject(MatDialog);
  ref?: MatDialogRef<ReauthDialogComponent, ReauthDialogResult | undefined>;

  open(data: ReauthDialogData): void {
    this.ref = this.dialog.open<
      ReauthDialogComponent,
      ReauthDialogData,
      ReauthDialogResult | undefined
    >(ReauthDialogComponent, { data, restoreFocus: true, autoFocus: 'first-tabbable' });
  }
}

async function setup(data: ReauthDialogData = { requireCode: true }) {
  await TestBed.configureTestingModule({
    imports: [DialogHost, MatDialogModule, NoopAnimationsModule, translations],
    providers: ZONELESS_TEST_PROVIDERS,
  }).compileComponents();
  const fixture = TestBed.createComponent(DialogHost);
  fixture.detectChanges();
  // In jsdom the CDK overlay focus trap needs a real rect to consider the trigger visible.
  vi.spyOn(HTMLElement.prototype, 'getClientRects').mockReturnValue([{}] as unknown as DOMRectList);
  // A real dialog is opened by an interaction: the trigger holds focus, so `restoreFocus` has somewhere to return to.
  (fixture.nativeElement as HTMLElement).querySelector<HTMLButtonElement>('#trigger')?.focus();
  fixture.componentInstance.open(data);
  fixture.detectChanges();
  await fixture.whenStable();
  const document = TestBed.inject(DOCUMENT);
  const dialog = document.querySelector('nts-reauth-dialog');
  const ref = fixture.componentInstance.ref;
  if (dialog === null || ref === undefined) throw new Error('dialog did not open');
  return { fixture, ref, dialog: dialog as HTMLElement, document };
}

function pushState(ref: MatDialogRef<ReauthDialogComponent>, state: Partial<ReauthDialogState>): void {
  ref.componentRef!.setInput('state', { ...REAUTH_DIALOG_IDLE, ...state });
}

describe('ReauthDialogComponent', () => {
  it('renders title, reason, password and code fields with the deck copy', async () => {
    const { dialog } = await setup();
    expect(dialog.querySelector('h2')?.textContent).toContain(reauthEn.title);
    expect(dialog.querySelector('p')?.textContent).toContain(reauthEn.reason);
    const password = dialog.querySelector<HTMLInputElement>('#reauth-password');
    expect(password?.getAttribute('autocomplete')).toBe('current-password');
    expect(password?.type).toBe('password');
    const code = dialog.querySelector<HTMLInputElement>('#reauth-code');
    expect(code?.getAttribute('autocomplete')).toBe('one-time-code');
    expect(code?.getAttribute('inputmode')).toBe('numeric');
  });

  it('hides the code field when requireCode is false', async () => {
    const { dialog } = await setup({ requireCode: false });
    expect(dialog.querySelector('#reauth-code')).toBeNull();
    expect(dialog.querySelector('#reauth-password')).not.toBeNull();
  });

  it('aria-labelledby points at the title and aria-describedby at the reason paragraph', async () => {
    const { document } = await setup();
    const container = document.querySelector('mat-dialog-container');
    const title = document.querySelector('.reauth-dialog__title');
    const reason = document.querySelector('.reauth-dialog__reason');
    expect(title?.id).toBeTruthy();
    expect(reason?.id).toBeTruthy();
    // Material pushes the title id into the container's `aria-labelledby` queue in a microtask and marks it for
    // check, so the host binding lands on the next scheduled change detection, not on `whenStable`.
    await vi.waitFor(() => expect(container?.getAttribute('aria-labelledby')).toBe(title?.id));
    expect(container?.getAttribute('aria-describedby')).toBe(reason?.id);
  });

  it('submit emits { password, code } and the dialog stays open (the service owns closing)', async () => {
    const { fixture, ref, dialog } = await setup();
    const submitted = vi.fn();
    const closed = vi.fn();
    onEmit(ref.componentInstance.submitted, submitted);
    onEmit(ref.afterClosed(), closed);

    const password = dialog.querySelector<HTMLInputElement>('#reauth-password')!;
    const code = dialog.querySelector<HTMLInputElement>('#reauth-code')!;
    password.value = 'hunter2';
    password.dispatchEvent(new Event('input'));
    code.value = '123456';
    code.dispatchEvent(new Event('input'));
    dialog.querySelector('form')?.dispatchEvent(new Event('submit', { bubbles: true, cancelable: true }));
    await fixture.whenStable();

    expect(submitted).toHaveBeenCalledWith({ password: 'hunter2', code: '123456' });
    expect(closed).not.toHaveBeenCalled();
  });

  it('empty password emits nothing and marks the field', async () => {
    const { fixture, ref, dialog } = await setup();
    const submitted = vi.fn();
    onEmit(ref.componentInstance.submitted, submitted);
    dialog.querySelector('form')?.dispatchEvent(new Event('submit', { bubbles: true, cancelable: true }));
    await fixture.whenStable();
    expect(submitted).not.toHaveBeenCalled();
    expect(dialog.querySelector('#reauth-password-error')?.textContent).toContain(
      errorsEn.validation_failed,
    );
  });

  it('Escape closes with no result (cancel semantics)', async () => {
    const { fixture, ref, document } = await setup();
    const closed = vi.fn();
    const submitted = vi.fn();
    onEmit(ref.afterClosed(), closed);
    onEmit(ref.componentInstance.submitted, submitted);
    document.body.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', keyCode: 27, bubbles: true }));
    await fixture.whenStable();
    expect(closed).toHaveBeenCalledWith(undefined);
    expect(submitted).not.toHaveBeenCalled();
  });

  it('restores focus to the trigger after close', async () => {
    const { fixture, ref, document, dialog } = await setup();
    const trigger = (fixture.nativeElement as HTMLElement).querySelector<HTMLButtonElement>('#trigger');
    expect(trigger).not.toBeNull();
    // CDK focuses the first tabbable on a timer; close only once the dialog owns focus, like a real Escape.
    await vi.waitFor(() => expect(dialog.contains(document.activeElement)).toBe(true));
    ref.close();
    await fixture.whenStable();
    expect(document.activeElement).toBe(trigger);
  });

  it('service-pushed field error paints without closing; form error paints the alert', async () => {
    const { ref, dialog, fixture } = await setup();
    pushState(ref, { errorKey: 'errors.invalid_credentials', errorField: 'password' });
    await fixture.whenStable();
    expect(dialog.querySelector('#reauth-password-error')?.textContent).toContain(
      errorsEn.invalid_credentials,
    );
    pushState(ref, { errorKey: 'errors.unknown_error', errorField: null, traceId: 't-9' });
    await fixture.whenStable();
    const alert = dialog.querySelector('#reauth-alert');
    expect(alert?.getAttribute('role')).toBe('alert');
    expect(alert?.textContent).toContain(errorsEn.unknown_error);
    expect(alert?.textContent).toContain('t-9');
  });

  it('429 shows the countdown and disables submit until retryAfterElapsed', async () => {
    const { ref, dialog, fixture } = await setup();
    const elapsed = vi.fn();
    onEmit(ref.componentInstance.retryAfterElapsed, elapsed);
    pushState(ref, { retryAfterSec: 30 });
    await fixture.whenStable();
    expect(dialog.querySelector('nts-retry-countdown')).not.toBeNull();
    const submit = dialog.querySelector<HTMLButtonElement>('[type="submit"]');
    expect(submit?.disabled).toBe(true);
    expect(submit?.getAttribute('aria-describedby')).toBe('reauth-retry');
  });
});
