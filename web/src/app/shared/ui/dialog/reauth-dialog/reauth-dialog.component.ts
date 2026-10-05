import { CdkTrapFocus } from '@angular/cdk/a11y';
import {
  ChangeDetectionStrategy,
  Component,
  ElementRef,
  afterNextRender,
  effect,
  inject,
  input,
  output,
} from '@angular/core';
import { type AbstractControl, FormControl, FormGroup, ReactiveFormsModule, Validators } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import {
  MAT_DIALOG_DATA,
  MatDialogClose,
  MatDialogRef,
  MatDialogTitle,
} from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { TranslocoPipe, provideTranslocoScope } from '@jsverse/transloco';
import { RetryCountdown } from '../../retry-countdown/retry-countdown.component';
import {
  REAUTH_DIALOG_IDLE,
  type ReauthDialogData,
  type ReauthDialogResult,
  type ReauthDialogState,
} from './reauth-dialog.model';

interface ReauthControls {
  readonly password: FormControl<string>;
  readonly code: FormControl<string>;
}

let nextReauthDialogId = 0;

/**
 * The shared re-auth dialog (10.3.3-T3, deck `f-reauth*`): `ReauthService` opens it through `MatDialog` with
 * `{ requireCode }`, pushes `ReauthDialogState` via `setInput`, owns the POST and decides close vs stay. This
 * component never calls the API and never closes itself on submit: it emits `{ password, code }` and shows the
 * field error or the form alert the service sends back. Cancel (`mat-dialog-close`), Escape and a backdrop click
 * close with no result. A 429 shows `RetryCountdown` and disables submit until it elapses.
 *
 * `MatDialog` names the container from `mat-dialog-title` (aria-labelledby); `aria-describedby` has no config, so
 * the constructor points the host's `mat-dialog-container` at the reason paragraph once.
 * `inject()` here is the shared-dialog exception (MAT_DIALOG_DATA, MatDialogRef, ElementRef), not store/service DI.
 */
@Component({
  selector: 'nts-reauth-dialog',
  imports: [
    ReactiveFormsModule,
    MatButtonModule,
    MatDialogClose,
    MatDialogTitle,
    MatFormFieldModule,
    MatInputModule,
    MatProgressSpinnerModule,
    TranslocoPipe,
    CdkTrapFocus,
    RetryCountdown,
  ],
  // 'core' is the root scope (always loaded); the dialog's own copy lives in the lazy 'reauth' scope (10 CR-51) and
  // the 'errors' route scope supplies failures.
  providers: [provideTranslocoScope('reauth', 'errors')],
  templateUrl: './reauth-dialog.component.html',
  styleUrl: './reauth-dialog.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class ReauthDialogComponent {
  private readonly data = inject<ReauthDialogData>(MAT_DIALOG_DATA, { optional: true });
  private readonly dialogRef = inject<MatDialogRef<ReauthDialogComponent>>(MatDialogRef, { optional: true });
  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef);

  readonly state = input<ReauthDialogState>(REAUTH_DIALOG_IDLE);

  /** `submitted` is emitted on every submit; the dialog stays open until the service closes it. */
  readonly submitted = output<ReauthDialogResult>();
  readonly retryAfterElapsed = output<void>();

  protected readonly requireCode = this.data?.requireCode ?? true;
  protected readonly titleId = `nts-reauth-title-${++nextReauthDialogId}`;
  protected readonly bodyId = `nts-reauth-body-${nextReauthDialogId}`;
  /** True when MatDialog owns the surrounding container (its role, aria-modal and aria-labelledby). */
  protected readonly insideMatDialog = this.dialogRef !== null;

  protected readonly form = new FormGroup<ReauthControls>({
    password: new FormControl('', {
      nonNullable: true,
      validators: [(control: AbstractControl) => Validators.required(control)],
    }),
    code: new FormControl('', { nonNullable: true }),
  });

  constructor() {
    if (this.requireCode) {
      this.form.controls.code.addValidators((control: AbstractControl) => Validators.required(control));
    }
    // A service-pushed field error (`errorField` + `errorKey`) must paint inside `mat-form-field`: Material projects
    // `mat-error` only while the control is in its error state, and the field may be untouched when the service
    // answers (the POST can fail before the user edits that field again).
    effect(() => {
      const { errorField, errorKey } = this.state();
      if (errorField !== null && errorKey !== null) this.form.controls[errorField].markAsTouched();
    });
    afterNextRender(() => {
      this.host.nativeElement.closest('mat-dialog-container')?.setAttribute('aria-describedby', this.bodyId);
    });
  }

  protected submit(): void {
    if (this.state().submitting || this.state().retryAfterSec !== null) return;
    this.form.markAllAsTouched();
    if (this.form.invalid) return;
    const raw = this.form.getRawValue();
    this.submitted.emit({ password: raw.password, code: this.requireCode ? raw.code : '' });
  }

  /** Escape is a cancel; while a POST is in flight it must not abandon the dialog (deck f-reauth-submitting). */
  protected onEscape(event: Event): void {
    if (this.state().submitting) {
      event.preventDefault();
      event.stopPropagation();
    } else if (!this.insideMatDialog) {
      this.form.reset({ password: '', code: '' });
    }
  }

  protected fieldError(field: 'password' | 'code'): string | null {
    const state = this.state();
    if (state.errorField === field && state.errorKey !== null) return state.errorKey;
    const control = this.form.controls[field];
    return control.invalid && control.touched ? 'errors.validation_failed' : null;
  }
}
