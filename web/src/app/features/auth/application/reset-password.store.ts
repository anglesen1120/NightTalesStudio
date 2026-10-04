import { DOCUMENT } from '@angular/common';
import { DestroyRef, Injectable, computed, effect, inject, signal, untracked } from '@angular/core';
import { Router } from '@angular/router';
import type { AppError } from '@core/errors';
import { SessionStore } from '@core/session';
import { clearRouterFragment, readTokenFromHash, stripHash } from '../data-access/reset-token';
import { formErrorKey, hasFieldErrors, retryAfterOf } from './auth-failure';
import { AuthRepository } from './auth.repository';

/** The form control the password field errors are bound to (the container's form uses the same name). */
const PASSWORD_POINTER = 'password';
/** The wire name of the same field in `ResetPasswordCommand`. */
const WIRE_PASSWORD_POINTER = 'newPassword';

/**
 * Field errors of a rejected new password, keyed by the form control: the wire pointer `newPassword` is renamed, and
 * `breached_password` (which may arrive without an `errors` map) is attached to the password control.
 */
function withPasswordField(error: AppError): AppError {
  const wire = error.fieldErrors[WIRE_PASSWORD_POINTER];
  if (wire !== undefined) {
    const fieldErrors: Record<string, readonly string[]> = { ...error.fieldErrors, [PASSWORD_POINTER]: wire };
    delete fieldErrors[WIRE_PASSWORD_POINTER];
    return { ...error, fieldErrors };
  }
  if (error.code === 'breached_password' && !hasFieldErrors(error)) {
    return { ...error, fieldErrors: { [PASSWORD_POINTER]: [error.code] } };
  }
  return error;
}

/**
 * State of `/reset-password`. The constructor reads the token from the URL fragment and strips the fragment at once,
 * before any request can exist. The token is a private field: not a signal, never storage, DOM or console.
 */
@Injectable()
export class ResetPasswordStore {
  private readonly repository = inject(AuthRepository);
  private readonly session = inject(SessionStore);
  private readonly document = inject(DOCUMENT);
  private readonly router = inject(Router);

  private token: string | null = null;
  private readonly hasTokenState = signal(false);
  private readonly submittingState = signal(false);
  private readonly errorState = signal<AppError | null>(null);
  private readonly formErrorState = signal<string | null>(null);
  private readonly mismatchState = signal(false);
  private readonly expiredState = signal(false);
  private readonly doneState = signal(false);
  private readonly retryAfterState = signal<number | null>(null);
  private readonly rateLimitedUntilState = signal<number | null>(null);
  private destroyed = false;

  readonly hasToken = this.hasTokenState.asReadonly();
  readonly submitting = this.submittingState.asReadonly();
  readonly error = this.errorState.asReadonly();
  readonly formError = this.formErrorState.asReadonly();
  readonly mismatch = this.mismatchState.asReadonly();
  /** Used, expired or unknown token: the page offers "request a new link". */
  readonly expired = this.expiredState.asReadonly();
  readonly done = this.doneState.asReadonly();
  readonly retryAfterSec = this.retryAfterState.asReadonly();
  readonly canSubmit = computed(
    () => this.hasTokenState() && !this.expiredState() && !this.doneState() && !this.submittingState() && this.rateLimitedUntilState() === null,
  );

  constructor() {
    this.token = readTokenFromHash(this.document.location.hash);
    stripHash();
    // The Router keeps its own copy of the fragment (`router.url`, route snapshots); drop it too.
    void clearRouterFragment(this.router);
    this.hasTokenState.set(this.token !== null);
    inject(DestroyRef).onDestroy(() => {
      this.destroyed = true;
      this.token = null;
    });
    // A session generation change abandons the in-flight request: its late result is ignored, so the flag is reset here.
    effect(() => {
      this.session.generation();
      untracked(() => this.submittingState.set(false));
    });
  }

  async submit(password: string, confirm: string): Promise<void> {
    const token = this.token;
    if (token === null || !this.canSubmit()) return;
    if (password !== confirm) {
      this.mismatchState.set(true);
      return;
    }
    const generation = this.session.generation();
    this.mismatchState.set(false);
    this.submittingState.set(true);
    this.errorState.set(null);
    this.formErrorState.set(null);
    const result = await this.repository.resetPassword(token, password);
    if (this.destroyed || generation !== this.session.generation()) return;
    this.submittingState.set(false);
    if (result.ok) {
      this.token = null;
      this.doneState.set(true);
      return;
    }
    this.fail(result.error);
  }

  /** The password or the confirmation changed: the previous mismatch verdict no longer applies. */
  clearMismatch(): void {
    this.mismatchState.set(false);
  }

  retryAfterElapsed(): void {
    this.retryAfterState.set(null);
    this.rateLimitedUntilState.set(null);
  }

  private fail(raw: AppError): void {
    const error = withPasswordField(raw);
    // A 400 `validation_failed` that names no password field rejects the token itself (used, expired, unknown).
    if (error.code === 'validation_failed' && error.fieldErrors[PASSWORD_POINTER] === undefined) {
      this.token = null;
      this.expiredState.set(true);
    }
    this.errorState.set(error);
    this.formErrorState.set(this.expiredState() ? null : formErrorKey(error));
    const seconds = retryAfterOf(error);
    this.retryAfterState.set(seconds);
    this.rateLimitedUntilState.set(seconds === null ? null : Date.now() + seconds * 1000);
  }
}
