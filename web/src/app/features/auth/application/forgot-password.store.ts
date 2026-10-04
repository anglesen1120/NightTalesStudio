import { DestroyRef, Injectable, computed, effect, inject, signal, untracked } from '@angular/core';
import type { AppError } from '@core/errors';
import { SessionStore } from '@core/session';
import { formErrorKey, retryAfterOf } from './auth-failure';
import { AuthRepository } from './auth.repository';

/**
 * State of `/forgot-password`. One request per `submit` (in-flight guard, no retry). `sent` is set by the success
 * of the call alone: the API answers 202 for known and unknown addresses, and nothing here branches on more.
 */
@Injectable()
export class ForgotPasswordStore {
  private readonly repository = inject(AuthRepository);
  private readonly session = inject(SessionStore);

  private readonly submittingState = signal(false);
  private readonly sentState = signal(false);
  private readonly errorState = signal<AppError | null>(null);
  private readonly formErrorState = signal<string | null>(null);
  private readonly retryAfterState = signal<number | null>(null);
  private readonly rateLimitedUntilState = signal<number | null>(null);
  private destroyed = false;

  readonly submitting = this.submittingState.asReadonly();
  readonly sent = this.sentState.asReadonly();
  readonly error = this.errorState.asReadonly();
  readonly formError = this.formErrorState.asReadonly();
  readonly retryAfterSec = this.retryAfterState.asReadonly();
  readonly canSubmit = computed(() => !this.submittingState() && this.rateLimitedUntilState() === null);

  constructor() {
    inject(DestroyRef).onDestroy(() => {
      this.destroyed = true;
    });
    // A session generation change abandons the in-flight request: its late result is ignored, so the flag is reset here.
    effect(() => {
      this.session.generation();
      untracked(() => this.submittingState.set(false));
    });
  }

  async submit(email: string): Promise<void> {
    if (this.submittingState() || this.rateLimitedUntilState() !== null) return;
    const generation = this.session.generation();
    this.submittingState.set(true);
    this.errorState.set(null);
    this.formErrorState.set(null);
    const result = await this.repository.forgotPassword(email);
    if (this.destroyed || generation !== this.session.generation()) return;
    this.submittingState.set(false);
    if (result.ok) {
      this.sentState.set(true);
      return;
    }
    this.errorState.set(result.error);
    this.formErrorState.set(formErrorKey(result.error));
    const seconds = retryAfterOf(result.error);
    this.retryAfterState.set(seconds);
    this.rateLimitedUntilState.set(seconds === null ? null : Date.now() + seconds * 1000);
  }

  retryAfterElapsed(): void {
    this.retryAfterState.set(null);
    this.rateLimitedUntilState.set(null);
  }
}
