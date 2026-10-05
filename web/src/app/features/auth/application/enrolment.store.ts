import { DestroyRef, Injectable, computed, effect, inject, signal, untracked } from '@angular/core';
import { Router } from '@angular/router';
import type { AppError, Result } from '@core/errors';
import { ReauthService, SessionStore } from '@core/session';
import type { EnrolmentStep, MfaSetup, MfaSetupViewModel, RecoveryCodes } from '../domain/mfa.model';
import { formErrorKey, retryAfterOf, traceIdOf } from './auth-failure';
import { MfaRepository } from './mfa.repository';

const LOGIN_URL = '/login';
const FIELD_LEVEL_CODES: Readonly<Record<string, true>> = { invalid_mfa_code: true, validation_failed: true };

/**
 * State of `/account/security/mfa-setup` (provided by the routed container, one instance per visit). One request at
 * a time. The setup secret, the otpauth URI and the recovery codes live in signals only: never logged, never in
 * storage, a URL or the DOM attributes; they are dropped on destroy and on any session end or identity change, and a
 * response that arrives after either paints nothing.
 *
 * Entered with the enrolment-only token (`SessionStore.mode() === 'enrolment'`) `setup`/`verify` need no re-auth
 * (WT41); from a full session both run through `ReauthService.withReauth` (dialog, then exactly one retry).
 */
@Injectable()
export class EnrolmentStore {
  private readonly repository = inject(MfaRepository);
  private readonly reauth = inject(ReauthService);
  private readonly session = inject(SessionStore);
  private readonly router = inject(Router);

  private readonly stepState = signal<EnrolmentStep>('loading');
  private readonly setupState = signal<MfaSetup | null>(null);
  private readonly codesState = signal<RecoveryCodes | null>(null);
  private readonly errorState = signal<AppError | null>(null);
  private readonly submittingState = signal(false);
  private readonly confirmedSavedState = signal(false);
  private readonly reauthRequiredState = signal(false);
  private readonly reLoginRequiredState = signal(false);
  private readonly retryAfterState = signal<number | null>(null);
  private readonly rateLimitedUntilState = signal<number | null>(null);
  private readonly enrolmentOnlyState = signal(this.session.mode() === 'enrolment');
  private destroyed = false;
  /** Generation created when a successful enrolment-only verify clears its token; its recovery codes stay visible. */
  private retainCodesThroughGeneration: number | null = null;

  readonly step = this.stepState.asReadonly();
  readonly setup = this.setupState.asReadonly();
  readonly codes = this.codesState.asReadonly();
  readonly error = this.errorState.asReadonly();
  readonly submitting = this.submittingState.asReadonly();
  readonly confirmedSaved = this.confirmedSavedState.asReadonly();
  readonly retryAfterSec = this.retryAfterState.asReadonly();
  readonly enrolmentOnly = this.enrolmentOnlyState.asReadonly();
  readonly canSubmit = computed(() => this.setupState() !== null && !this.submittingState() && this.rateLimitedUntilState() === null);

  /** i18n key of the non-field alert; `null` for field-level errors (the container binds those to the code control). */
  readonly formError = computed(() => {
    const error = this.errorState();
    if (error === null || FIELD_LEVEL_CODES[error.code] === true || this.reLoginRequiredState() || this.reauthRequiredState()) return null;
    return formErrorKey(error);
  });

  readonly viewModel = computed<MfaSetupViewModel>(() => ({
    step: this.stepState(),
    setup: this.setupState(),
    codes: this.codesState(),
    enrolmentOnly: this.enrolmentOnlyState(),
    submitting: this.submittingState(),
    canSubmit: this.canSubmit(),
    formError: this.formError(),
    traceId: traceIdOf(this.errorState()),
    retryAfterSec: this.retryAfterState(),
    confirmedSaved: this.confirmedSavedState(),
    reauthRequired: this.reauthRequiredState(),
    reLoginRequired: this.reLoginRequiredState(),
  }));

  constructor() {
    inject(DestroyRef).onDestroy(() => {
      this.destroyed = true;
      this.dropSecrets();
    });
    // An external session end or identity change drops every secret. The exact generation created when a successful
    // enrolment-only verify clears its narrow token is the sole exception: its one-time codes must remain saveable.
    effect(() => {
      this.session.endReason();
      const generation = this.session.generation();
      untracked(() => {
        if (generation === this.retainCodesThroughGeneration && this.stepState() === 'codes') {
          this.retainCodesThroughGeneration = null;
          return;
        }
        this.retainCodesThroughGeneration = null;
        this.dropSecrets();
        this.submittingState.set(false);
      });
    });
  }

  /**
   * `POST /me/mfa/setup`. A `409 concurrency_conflict` (a stale pending secret) re-runs the call once, never a loop;
   * a `404 not_found` on the enrolment token means it expired (`reLoginRequired`); a re-auth the user cancelled leaves
   * `reauthRequired` with a retry (call `load()` again).
   */
  async load(): Promise<void> {
    if (this.submittingState()) return;
    const generation = this.session.generation();
    this.enrolmentOnlyState.set(this.session.mode() === 'enrolment');
    this.resetFeedback();
    this.dropSecrets();
    this.stepState.set('loading');
    this.submittingState.set(true);
    let result = await this.callSetup();
    if (this.isStale(generation)) return;
    if (!result.ok && result.error.code === 'concurrency_conflict') {
      result = await this.callSetup();
      if (this.isStale(generation)) return;
    }
    this.submittingState.set(false);
    if (!result.ok) {
      this.stepState.set(result.error.code === 'reauth_required' ? 'show' : 'error');
      this.fail(result.error);
      return;
    }
    this.setupState.set(result.value);
    this.stepState.set('show');
  }

  /** `POST /me/mfa/verify { code }` → exactly 10 recovery codes (`step 'codes'`); the secret is dropped with them. */
  async verify(rawCode: string): Promise<void> {
    const code = rawCode.trim();
    const step = this.stepState();
    if (code === '' || (step !== 'show' && step !== 'verify') || !this.canSubmit()) return;
    const generation = this.session.generation();
    this.resetFeedback();
    this.stepState.set('verify');
    this.submittingState.set(true);
    const call = () => this.repository.verify(code);
    const result = await (this.enrolmentOnlyState() ? call() : this.reauth.withReauth(call));
    if (this.isStale(generation)) return;
    this.submittingState.set(false);
    if (!result.ok) {
      this.fail(result.error);
      return;
    }
    this.setupState.set(null);
    this.codesState.set(result.value);
    this.confirmedSavedState.set(false);
    this.stepState.set('codes');
    if (this.enrolmentOnlyState()) {
      // The verify response is the last request allowed for this narrow token. Clear it immediately, while retaining
      // the one-time codes in this page-scoped store until the owner confirms that they have saved them.
      this.retainCodesThroughGeneration = this.session.generation() + 1;
      this.session.reset();
    }
  }

  /**
   * Regeneration: re-authenticate FIRST (dialog), and only on success `POST /me/mfa/recovery-codes`. A failed or
   * cancelled re-auth issues no request and leaves the old codes valid; the outcome is shown, never silent.
   */
  async regenerate(): Promise<void> {
    if (this.submittingState()) return;
    const generation = this.session.generation();
    this.resetFeedback();
    this.submittingState.set(true);
    const reauthenticated = await this.reauth.ensureReauthenticated();
    if (this.isStale(generation)) return;
    if (!reauthenticated.ok) {
      this.submittingState.set(false);
      this.fail(reauthenticated.error);
      return;
    }
    const result = await this.repository.regenerateRecoveryCodes();
    if (this.isStale(generation)) return;
    this.submittingState.set(false);
    if (!result.ok) {
      this.fail(result.error);
      return;
    }
    this.codesState.set(result.value);
    this.confirmedSavedState.set(false);
    this.stepState.set('codes');
  }

  /** The "I saved my recovery codes" checkbox. */
  setConfirmedSaved(value: boolean): void {
    this.confirmedSavedState.set(value);
  }

  /** The primary action: allowed only after the checkbox. Drops the codes for good; there is no second reveal. */
  finish(): boolean {
    if (this.stepState() !== 'codes' || !this.confirmedSavedState()) return false;
    this.codesState.set(null);
    this.stepState.set('done');
    return true;
  }

  /** The countdown reached 0: submit is allowed again. */
  retryAfterElapsed(): void {
    this.retryAfterState.set(null);
    this.rateLimitedUntilState.set(null);
  }

  /**
   * Leaves for `/login` (expired or spent enrolment token, or after enrolment). The enrolment token is cleared first
   * with the existing `SessionStore.reset()`: it drops every credential and the enrolment token without an end reason
   * (so `/login` shows no "signed out" banner), without a server call (the token has no refresh cookie, `logout()`
   * would POST with none) and without navigating; otherwise `anonymousGuard` bounces `/login` back to mfa-setup.
   */
  async leaveToLogin(): Promise<void> {
    if (this.session.mode() === 'enrolment') this.session.reset();
    await this.router.navigateByUrl(LOGIN_URL);
  }

  private callSetup(): Promise<Result<MfaSetup>> {
    const call = () => this.repository.setup();
    return this.enrolmentOnlyState() ? call() : this.reauth.withReauth(call);
  }

  private fail(error: AppError): void {
    this.errorState.set(error);
    const seconds = retryAfterOf(error);
    this.retryAfterState.set(seconds);
    this.rateLimitedUntilState.set(seconds === null ? null : Date.now() + seconds * 1000);
    if (error.code === 'reauth_required') this.reauthRequiredState.set(true);
    // The enrolment token is spent or expired (15 min): the next call answers 404. No retry.
    if (error.code === 'not_found' && this.enrolmentOnlyState()) this.reLoginRequiredState.set(true);
  }

  private resetFeedback(): void {
    this.errorState.set(null);
    this.reauthRequiredState.set(false);
    this.reLoginRequiredState.set(false);
    this.retryAfterState.set(null);
    this.rateLimitedUntilState.set(null);
  }

  private dropSecrets(): void {
    this.setupState.set(null);
    this.codesState.set(null);
    this.confirmedSavedState.set(false);
  }

  /** A result after destroy or after the session identity changed paints nothing. */
  private isStale(generation: number): boolean {
    return this.destroyed || generation !== this.session.generation();
  }
}
