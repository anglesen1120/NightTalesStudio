import { Injectable, Injector, effect, inject, signal, untracked } from '@angular/core';
import { MatDialog, type MatDialogRef } from '@angular/material/dialog';
import { messageKeyOf, type AppError, type Result } from '@core/errors';
import { REAUTH_DIALOG_IDLE, ReauthDialogComponent, type ReauthDialogResult, type ReauthDialogState } from '@shared/ui';
import { ReauthPort } from './reauth.port';
import type { SessionScoped } from './session-scope';
import { SessionStore } from './session-store';

/**
 * Owners/admins always have TOTP in P, so the dialog always asks for the code. The case of an account without TOTP
 * (the dialog supports `requireCode: false`) belongs to the S phase; nothing here derives it from the token.
 */
const REQUIRE_CODE = true;

/**
 * The client-side outcomes that have no HTTP response (`status` 0, as `AppError` documents). Neither code is added to
 * `ERROR_CODES`: both already exist there.
 * - Cancelled: the owner closed the dialog (Escape/Cancel). The action still needs a re-auth, so it is `reauth_required`;
 *   a caller shows its "re-auth required" state with a retry button (task failure table), never a silent failure.
 * - Ended: the session ended or was replaced while the dialog was open (logout, revoked, sign-in as someone else).
 */
const CANCELLED: AppError = { code: 'reauth_required', status: 0, kind: 'auth', fieldErrors: {} };
const ENDED: AppError = { code: 'session_revoked', status: 0, kind: 'auth', fieldErrors: {} };

interface Flight {
  /** `SessionStore.generation()` captured when the dialog opened; the flight never outlives it. */
  readonly generation: number;
  readonly ref: MatDialogRef<ReauthDialogComponent, void>;
  readonly promise: Promise<Result<void>>;
  readonly resolve: (result: Result<void>) => void;
  /** `OutputRefSubscription` (outputs) and rxjs `Subscription` (`afterClosed`) both expose `unsubscribe()`. */
  readonly subscriptions: { unsubscribe(): void }[];
  state: ReauthDialogState;
  submitting: boolean;
  done: boolean;
}

function fail(error: AppError): Result<never> {
  return { ok: false, error };
}

/**
 * The root re-auth owner (10.3.3-T3, README D6/D11): a `401 reauth_required` becomes ONE password + code dialog per
 * session generation, whatever the number of concurrent callers. It writes no client re-auth state (WR8: the server
 * stamps `reauthenticatedAt` on the access token it issues; the client keeps nothing and replays at most once).
 *
 * The flight dies with its session: `reset()` (the `SESSION_SCOPED` entry, run by `SessionStore.signOut`) and a changed
 * `generation()` (sign-in over the dialog) close the dialog and resolve every waiter with `ENDED`; a response that
 * arrives after that is ignored and can never resolve `ok`. Password and code live only in the call to the port: never
 * stored, never logged.
 *
 * A 401 `session_revoked` on the POST needs no `signOut` here: the session interceptor already signs the session out
 * for that code (`PASS_THROUGH_CODES`) before the error reaches the repository, and that runs `reset()`.
 */
@Injectable({ providedIn: 'root' })
export class ReauthService implements SessionScoped {
  private readonly dialog = inject(MatDialog);
  // Resolved on the first submit, not at construction: `SESSION_SCOPED.reset()` instantiates this service on every
  // sign-out, and that must not pull the HTTP repository (and `HttpClient`) in for a dialog that never opened.
  private readonly injector = inject(Injector);
  private readonly session = inject(SessionStore);

  private flight: Flight | null = null;
  private readonly openState = signal(false);

  readonly isDialogOpen = this.openState.asReadonly();

  constructor() {
    // Sign-in over an open dialog bumps the generation without a `reset()`: end the flight of the old session.
    effect(() => {
      const generation = this.session.generation();
      untracked(() => {
        const flight = this.flight;
        if (flight !== null && flight.generation !== generation) this.settle(flight, fail(ENDED));
      });
    });
  }

  /**
   * Resolves `ok` once the user re-authenticated, else `err`: the server's error for `session_revoked` (the session
   * ends), `CANCELLED` when the dialog was dismissed, `ENDED` when the session went away. Concurrent callers share one
   * dialog and one promise. Without a full session (anonymous, enrolment-only token) no dialog opens: there is
   * nothing to re-authenticate (the enrolment page never asks, WT41).
   */
  ensureReauthenticated(): Promise<Result<void>> {
    const generation = this.session.generation();
    const current = this.flight;
    if (current !== null) {
      if (current.generation === generation) return current.promise;
      this.settle(current, fail(ENDED));
    }
    if (!this.session.isAuthenticated()) return Promise.resolve(fail(ENDED));
    return this.open(generation).promise;
  }

  /**
   * Runs `action`; on `reauth_required` asks for a re-auth and runs it exactly once more. Any other outcome, a failed
   * or cancelled re-auth, and a session that changed meanwhile (the replay would belong to another session) return
   * the error unchanged.
   */
  async withReauth<T>(action: () => Promise<Result<T>>): Promise<Result<T>> {
    const generation = this.session.generation();
    const first = await action();
    if (first.ok || first.error.code !== 'reauth_required') return first;
    const reauth = await this.ensureReauthenticated();
    if (!reauth.ok) return fail(reauth.error);
    if (this.session.generation() !== generation) return fail(ENDED);
    return action();
  }

  /** `SessionScoped`: logout / session end closes the dialog and rejects every waiter. */
  reset(): void {
    const flight = this.flight;
    if (flight !== null) this.settle(flight, fail(ENDED));
  }

  private open(generation: number): Flight {
    let resolve!: (result: Result<void>) => void;
    const promise = new Promise<Result<void>>((r) => {
      resolve = r;
    });
    const ref = this.dialog.open<ReauthDialogComponent, { requireCode: boolean }, void>(ReauthDialogComponent, {
      data: { requireCode: REQUIRE_CODE },
      restoreFocus: true,
      autoFocus: 'first-tabbable',
    });
    const flight: Flight = {
      generation,
      ref,
      promise,
      resolve,
      subscriptions: [],
      state: REAUTH_DIALOG_IDLE,
      submitting: false,
      done: false,
    };
    this.flight = flight;
    this.openState.set(true);

    const component = ref.componentInstance;
    flight.subscriptions.push(
      component.submitted.subscribe((result) => {
        void this.submit(flight, result);
      }),
      component.retryAfterElapsed.subscribe(() => {
        if (!flight.done) this.push(flight, REAUTH_DIALOG_IDLE);
      }),
      // Escape / Cancel (`mat-dialog-close`) / backdrop: closed without a success.
      ref.afterClosed().subscribe(() => {
        this.settle(flight, fail(CANCELLED));
      }),
    );
    return flight;
  }

  private async submit(flight: Flight, result: ReauthDialogResult): Promise<void> {
    // One POST per submit: ignore a repeat while one is in flight and while a 429 countdown disables the button.
    if (flight.done || flight.submitting || flight.state.retryAfterSec !== null) return;
    flight.submitting = true;
    this.push(flight, { ...flight.state, submitting: true, errorKey: null, errorField: null, traceId: null });

    const outcome = await this.injector.get(ReauthPort).reauth(result.password, result.code);
    flight.submitting = false;

    if (flight.done) return; // closed or ended meanwhile: a late answer paints nothing
    if (this.session.generation() !== flight.generation) {
      this.settle(flight, fail(ENDED)); // a late 200 of an older session never resolves ok
      return;
    }
    if (outcome.ok) {
      this.settle(flight, { ok: true, value: undefined });
      return;
    }
    const error = outcome.error;
    if (error.code === 'session_revoked') {
      this.settle(flight, fail(error));
      return;
    }
    this.push(flight, this.failureState(error));
  }

  private failureState(error: AppError): ReauthDialogState {
    const traceId = error.traceId ?? null;
    if (error.code === 'invalid_credentials') {
      return { submitting: false, errorKey: messageKeyOf(error), errorField: 'password', traceId, retryAfterSec: null };
    }
    if (error.code === 'invalid_mfa_code') {
      return { submitting: false, errorKey: messageKeyOf(error), errorField: 'code', traceId, retryAfterSec: null };
    }
    if (error.kind === 'rate_limit') {
      return { submitting: false, errorKey: messageKeyOf(error), errorField: null, traceId, retryAfterSec: error.retryAfterSec ?? null };
    }
    return { submitting: false, errorKey: 'reauth.failed', errorField: null, traceId, retryAfterSec: null };
  }

  private push(flight: Flight, state: ReauthDialogState): void {
    flight.state = state;
    flight.ref.componentRef?.setInput('state', state);
  }

  /** Ends the flight once: detaches, closes the dialog, resolves every waiter. A second call is a no-op. */
  private settle(flight: Flight, result: Result<void>): void {
    if (flight.done) return;
    flight.done = true;
    for (const subscription of flight.subscriptions) subscription.unsubscribe();
    if (this.flight === flight) {
      this.flight = null;
      this.openState.set(false);
    }
    flight.ref.close();
    flight.resolve(result);
  }
}
