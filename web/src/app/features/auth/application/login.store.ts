import { DestroyRef, Injectable, computed, effect, inject, signal, untracked } from '@angular/core';
import { ActivatedRoute, Router } from '@angular/router';
import type { AppError } from '@core/errors';
import { SessionStore, sanitizeReturnUrl } from '@core/session';
import type { LoginOutcome, LoginStep } from '../domain/auth.model';
import { formErrorKey, retryAfterOf } from './auth-failure';
import { AuthRepository } from './auth.repository';

const MFA_SETUP_URL = '/account/security/mfa-setup';

/**
 * State of the password step of `/login` (provided by the routed container, one instance per page). One request at a
 * time; a result that arrives after the page is gone, or after the session generation moved on, paints nothing and
 * signs nobody in. The `mfaToken` lives in this signal only (never storage, never the DOM). The `Retry-After`
 * countdown is ticked by `RetryCountdown`; the store holds the seconds and the deadline.
 */
@Injectable()
export class LoginStore {
  private readonly repository = inject(AuthRepository);
  private readonly session = inject(SessionStore);
  private readonly router = inject(Router);
  private readonly route = inject(ActivatedRoute);

  private readonly stepState = signal<LoginStep>('password');
  private readonly submittingState = signal(false);
  private readonly errorState = signal<AppError | null>(null);
  private readonly formErrorState = signal<string | null>(null);
  private readonly mfaTokenState = signal<string | null>(null);
  private readonly retryAfterState = signal<number | null>(null);
  private readonly rateLimitedUntilState = signal<number | null>(null);
  private destroyed = false;
  private readonly userSignedInState = signal(false);

  readonly step = this.stepState.asReadonly();
  readonly submitting = this.submittingState.asReadonly();
  readonly error = this.errorState.asReadonly();
  /** i18n key of the non-field alert; `null` when the error is field-level or absent. */
  readonly formError = this.formErrorState.asReadonly();
  readonly mfaToken = this.mfaTokenState.asReadonly();
  readonly retryAfterSec = this.retryAfterState.asReadonly();
  /** Epoch ms until which submit is refused (429). */
  readonly rateLimitedUntil = this.rateLimitedUntilState.asReadonly();
  readonly canSubmit = computed(() => !this.submittingState() && this.rateLimitedUntilState() === null);

  /**
   * `true` once this page's own password step signed the user in. The container's boot-refresh-race redirect must not
   * fire for that transition: the sign-in navigates exactly once, to the sanitised `returnUrl` [WR8].
   */
  readonly userSignedIn = this.userSignedInState.asReadonly();

  constructor() {
    inject(DestroyRef).onDestroy(() => {
      this.destroyed = true;
      this.mfaTokenState.set(null);
    });
    // A session end (logout, interceptor sign-out) or any identity change drops the challenge and the last error.
    effect(() => {
      this.session.endReason();
      this.session.generation();
      untracked(() => {
        this.clearChallenge();
        // The request of the previous identity is abandoned: its late result is ignored, so the flag is reset here.
        this.submittingState.set(false);
      });
    });
  }

  async submitPassword(email: string, password: string): Promise<void> {
    if (this.submittingState() || this.rateLimitedUntilState() !== null) return;
    const generation = this.session.generation();
    const returnUrl = this.route.snapshot.queryParamMap.get('returnUrl');
    this.mfaTokenState.set(null);
    this.submittingState.set(true);
    this.errorState.set(null);
    this.formErrorState.set(null);
    const result = await this.repository.login(email, password);
    if (this.destroyed || generation !== this.session.generation()) return;
    this.submittingState.set(false);
    if (!result.ok) {
      this.fail(result.error);
      return;
    }
    await this.proceed(result.value, returnUrl);
  }

  /** The countdown reached 0: submit is allowed again. */
  retryAfterElapsed(): void {
    this.retryAfterState.set(null);
    this.rateLimitedUntilState.set(null);
  }

  backToPassword(): void {
    this.mfaTokenState.set(null);
    this.stepState.set('password');
    this.clearError();
  }

  clearError(): void {
    this.errorState.set(null);
    this.formErrorState.set(null);
  }

  private clearChallenge(): void {
    this.mfaTokenState.set(null);
    this.stepState.set('password');
    this.clearError();
  }

  private fail(error: AppError): void {
    this.errorState.set(error);
    this.formErrorState.set(formErrorKey(error));
    const seconds = retryAfterOf(error);
    // A second 429 replaces the value and the deadline; there is no timer here to stack.
    this.retryAfterState.set(seconds);
    this.rateLimitedUntilState.set(seconds === null ? null : Date.now() + seconds * 1000);
  }

  private async proceed(outcome: LoginOutcome, returnUrl: string | null): Promise<void> {
    switch (outcome.kind) {
      case 'ok': {
        const { accessToken, expiresAt, role, amr } = outcome.session;
        this.userSignedInState.set(true);
        this.session.signIn({ accessToken, expiresAt }, role, amr);
        await this.router.navigateByUrl(sanitizeReturnUrl(returnUrl));
        return;
      }
      case 'mfa':
        // T3 widens `LoginStep` to show the code step; T1 only holds the challenge in memory.
        this.mfaTokenState.set(outcome.mfaToken);
        return;
      case 'enrolment':
        this.session.beginEnrolment(outcome.token);
        await this.router.navigateByUrl(MFA_SETUP_URL);
        return;
    }
  }
}
