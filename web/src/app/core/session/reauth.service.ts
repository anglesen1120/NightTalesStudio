import { Injectable, Injector, effect, inject, signal, untracked, type Type } from '@angular/core';
import type { MatDialog, MatDialogRef } from '@angular/material/dialog';
import { messageKeyOf, type AppError, type Result } from '@core/errors';
import type { ReauthDialogComponent } from '@shared/ui/dialog/reauth-dialog/reauth-dialog.component';
import {
  REAUTH_DIALOG_IDLE,
  type ReauthDialogData,
  type ReauthDialogResult,
  type ReauthDialogState,
} from '@shared/ui/dialog/reauth-dialog/reauth-dialog.model';
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
  /** `SessionStore.generation()` captured when the flight registered; the flight never outlives it. */
  readonly generation: number;
  /** `null` while the lazily imported dialog module is still loading; never read without a null check. */
  ref: MatDialogRef<ReauthDialogComponent, void> | null;
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
  // `Injector`, not `inject(MatDialog)`: nothing else eagerly loads `@angular/material/dialog`, so the service
  // resolves it (and the lazily imported component module) only at first open, keeping both out of the initial
  // chunk — the same lazy pattern as `ReauthPort`, resolved below so construction on `SESSION_SCOPED.reset()`
  // never pulls in the HTTP repository (PERF-1).
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
    // Re-check before touching the flight: a session that ended or was replaced while `action` ran must never
    // open a dialog (SEC-2); the post-dialog check below still guards the dialog window.
    if (this.session.generation() !== generation) return fail(ENDED);
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

  /**
   * Registers the flight synchronously — before the lazy imports below start — so a second caller arriving while
   * the module loads shares this flight: one promise, one dialog. The component module and `@angular/material/dialog`
   * load on first open only (PERF-1); a reset or generation change that lands meanwhile settles the flight in
   * `attachDialog` before anything opens.
   */
  private open(generation: number): Flight {
    let resolve!: (result: Result<void>) => void;
    const promise = new Promise<Result<void>>((r) => {
      resolve = r;
    });
    const flight: Flight = {
      generation,
      ref: null,
      promise,
      resolve,
      subscriptions: [],
      state: REAUTH_DIALOG_IDLE,
      submitting: false,
      done: false,
    };
    this.flight = flight;
    // Captured before the lazy imports: by the time the dialog attaches, focus may already have moved.
    const trigger = typeof document !== 'undefined' && document.activeElement instanceof HTMLElement ? document.activeElement : null;
    void this.attachDialog(flight, trigger);
    return flight;
  }

  private async attachDialog(flight: Flight, trigger: HTMLElement | null): Promise<void> {
    let component: Type<ReauthDialogComponent>;
    let dialog: MatDialog;
    try {
      const [componentModule, dialogModule] = await Promise.all([
        import('@shared/ui/dialog/reauth-dialog/reauth-dialog.component'),
        import('@angular/material/dialog'),
      ]);
      component = componentModule.ReauthDialogComponent;
      dialog = this.injector.get(dialogModule.MatDialog);
    } catch {
      // A failed chunk load ends the waiters with a catalogue code (`network_error` renders), never a silent hang.
      this.settle(flight, fail({ code: 'network_error', status: 0, kind: 'network', fieldErrors: {} }));
      return;
    }
    // The flight ended while the imports were pending (reset, sign-out) or the generation changed under it:
    // nothing may open for a flight of another session.
    if (flight.done) return;
    if (this.session.generation() !== flight.generation) {
      this.settle(flight, fail(ENDED));
      return;
    }

    try {
      const ref = dialog.open<ReauthDialogComponent, ReauthDialogData, void>(component, {
        data: { requireCode: REQUIRE_CODE },
        restoreFocus: trigger !== null && trigger !== document.body ? trigger : true,
        autoFocus: '#reauth-password',
        ariaModal: true,
      });
      flight.ref = ref;
      this.openState.set(true);

      flight.subscriptions.push(
        ref.componentInstance.submitted.subscribe((result) => {
          void this.submit(flight, result);
        }),
        ref.componentInstance.retryAfterElapsed.subscribe(() => {
          if (!flight.done) this.push(flight, REAUTH_DIALOG_IDLE);
        }),
        // Escape / Cancel (`mat-dialog-close`) / backdrop: closed without a success.
        ref.afterClosed().subscribe(() => {
          this.settle(flight, fail(CANCELLED));
        }),
      );
    } catch {
      // A synchronous dialog/setup failure must settle the registered flight, never leave later callers hanging.
      this.settle(flight, fail({ code: 'network_error', status: 0, kind: 'network', fieldErrors: {} }));
    }
  }

  private async submit(flight: Flight, result: ReauthDialogResult): Promise<void> {
    // One POST per submit: ignore a repeat while one is in flight and while a 429 countdown disables the button.
    if (flight.done || flight.submitting || flight.state.retryAfterSec !== null) return;
    const ref = flight.ref;
    if (ref === null) return; // unreachable: `submitted` only fires after `attachDialog` set the ref
    // Normalise once, here: display spaces in a pasted code never reach the port. A whitespace-only code sends nothing
    // (it would be charged as a wrong code against the MFA attempt window).
    const code = result.code.replace(/\s+/g, '');
    if (REQUIRE_CODE && code === '') return;
    flight.submitting = true;
    // Block the backdrop too while the POST is in flight: Escape/Cancel are already disabled by the dialog, and a
    // backdrop close would settle the flight CANCELLED while the request may already have succeeded (CR-3).
    ref.disableClose = true;
    this.push(flight, { ...flight.state, submitting: true, errorKey: null, errorField: null, traceId: null });

    const outcome = await this.injector.get(ReauthPort).reauth(result.password, code);
    flight.submitting = false;
    ref.disableClose = false;

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
    flight.ref?.componentRef?.setInput('state', state);
  }

  /** Ends the flight once: detaches, closes the dialog if it opened, resolves every waiter. A second call is a no-op. */
  private settle(flight: Flight, result: Result<void>): void {
    if (flight.done) return;
    flight.done = true;
    for (const subscription of flight.subscriptions) subscription.unsubscribe();
    if (this.flight === flight) {
      this.flight = null;
      this.openState.set(false);
    }
    flight.ref?.close();
    flight.resolve(result);
  }
}
