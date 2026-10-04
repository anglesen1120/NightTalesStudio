import { Injectable, Injector, computed, inject, isDevMode, signal } from '@angular/core';
import { Router } from '@angular/router';
import { AppErrorMapper, type AppError } from '@core/errors';
import { ActivityTracker } from './activity';
import type { MeSummary } from './me-summary';
import { RefreshLock } from './refresh-lock';
import { sanitizeReturnUrl } from './return-url';
import type { SessionEndReason } from './session-end';
import { SESSION_SCOPED } from './session-scope';
import { SessionApi } from './session.api';
import { FRESHNESS_MARGIN_MS, type PlatformRole, type SessionUser, type TokenPair } from './token-pair';

export type SessionStatus = 'anonymous' | 'restoring' | 'authenticated' | 'refreshing' | 'ended';
export type SessionMode = 'full' | 'enrolment';

export interface EnsureFreshOptions {
  /** The call comes from a user action: refresh even while the tab is idle (WR11). */
  readonly userAction?: boolean;
  /** A token the server just rejected with 401: when it is still the current one, refresh although it looks fresh. */
  readonly rejectedToken?: string;
}

interface FlightOptions {
  readonly rejectedToken: string | undefined;
  /** Boot restore or a user-action retry after `restore_failed`: a failure is classified by `endRestore` instead
   *  of ending the session with a redirect. */
  readonly restoring: boolean;
}

function isPlatformRole(value: unknown): value is PlatformRole {
  return value === 'user' || value === 'admin' || value === 'owner';
}

/**
 * Reads `role` and `amr` from the middle segment of a JWT. No signature check: the server is the authority, this is
 * only the view the UI needs. Anything unparsable is `null` (treated as no usable session).
 */
function readClaims(token: string): SessionUser | null {
  const segment = token.split('.')[1];
  if (segment === undefined || segment === '') return null;
  try {
    const padded = segment.replace(/-/g, '+').replace(/_/g, '/').padEnd(Math.ceil(segment.length / 4) * 4, '=');
    const claims: unknown = JSON.parse(atob(padded));
    if (typeof claims !== 'object' || claims === null) return null;
    const { role, amr } = claims as Record<string, unknown>;
    if (!isPlatformRole(role)) return null;
    const methods = typeof amr === 'string' ? [amr] : Array.isArray(amr) ? amr.filter((m): m is string => typeof m === 'string') : [];
    return { role, amr: methods };
  } catch {
    return null;
  }
}

function isAppError(value: unknown): value is AppError {
  return typeof value === 'object' && value !== null && 'code' in value && 'status' in value && 'kind' in value;
}

/**
 * The in-memory session (README D6): access token, role, profile view, single-flight refresh. Tokens never touch
 * browser storage. Every refresh/restore captures `generation` before it awaits, so a late result of an older
 * session can neither commit state nor end a newer one.
 */
@Injectable({ providedIn: 'root' })
export class SessionStore {
  private readonly api = inject(SessionApi);
  private readonly lock = inject(RefreshLock);
  private readonly mapper = inject(AppErrorMapper);
  private readonly injector = inject(Injector);
  private readonly scopes = inject(SESSION_SCOPED, { optional: true }) ?? [];

  private readonly accessTokenState = signal<string | null>(null);
  private readonly expiresAtState = signal<number | null>(null);
  private readonly roleState = signal<PlatformRole | null>(null);
  private readonly amrState = signal<readonly string[]>([]);
  private readonly meState = signal<MeSummary | null>(null);
  private readonly enrolmentTokenState = signal<string | null>(null);
  private readonly endReasonState = signal<SessionEndReason | null>(null);
  private readonly restoreSettledState = signal(false);
  private readonly restoreStartedState = signal(false);
  private readonly refreshingState = signal(false);
  private readonly generationState = signal(0);

  private refreshInFlight: Promise<string | null> | null = null;
  private restorePromise: Promise<void> | null = null;

  readonly accessToken = this.accessTokenState.asReadonly();
  readonly expiresAt = this.expiresAtState.asReadonly();
  readonly role = this.roleState.asReadonly();
  readonly amr = this.amrState.asReadonly();
  readonly me = this.meState.asReadonly();
  readonly enrolmentToken = this.enrolmentTokenState.asReadonly();
  readonly endReason = this.endReasonState.asReadonly();
  readonly restoreSettled = this.restoreSettledState.asReadonly();
  readonly generation = this.generationState.asReadonly();

  readonly mode = computed<SessionMode>(() => (this.enrolmentTokenState() === null ? 'full' : 'enrolment'));

  /** `'refreshing'`/`'restoring'` are transient and never a sign-out; enrolment mode is never `'authenticated'`. */
  readonly status = computed<SessionStatus>(() => {
    if (this.enrolmentTokenState() !== null) return 'anonymous';
    if (this.accessTokenState() !== null) return this.refreshingState() ? 'refreshing' : 'authenticated';
    if (this.restoreStartedState() && !this.restoreSettledState()) return 'restoring';
    return this.endReasonState() === null ? 'anonymous' : 'ended';
  });

  readonly isAuthenticated = computed(() => this.accessTokenState() !== null && this.enrolmentTokenState() === null);
  readonly isOwnerOrAdmin = computed(() => this.isAuthenticated() && this.roleState() !== 'user' && this.roleState() !== null);

  /** Boot restore, memoized: one refresh with the cookie. Failures settle classified (anonymous or ended); never rejects. */
  restore(): Promise<void> {
    this.restorePromise ??= this.runRestore();
    return this.restorePromise;
  }

  /**
   * The token to present, refreshing on demand (D6/D7, no timer). `null` without any HTTP call when the session is
   * ended, there is nothing to refresh, or a background caller meets an idle tab. Rejects with the refresh's
   * `AppError` when a refresh was attempted and failed. The one ended state a call may leave is `restore_failed`:
   * transient, the cookie may still be valid, so a `userAction` call retries the refresh once more and re-applies
   * the boot classification on failure (failure table L288).
   */
  async ensureFreshAccessToken(opts?: EnsureFreshOptions): Promise<string | null> {
    if (!this.restoreSettledState()) await this.restore();
    const ended = this.endReasonState();
    if (ended !== null) {
      if (ended !== 'restore_failed' || opts?.userAction !== true) return null;
      return this.startFlight({ rejectedToken: undefined, restoring: true });
    }
    const token = this.accessTokenState();
    if (token === null) return null;
    if (!this.isStale(token, opts?.rejectedToken)) return token;
    if (opts?.userAction !== true && !this.injector.get(ActivityTracker).isActive()) return null;
    return this.startFlight({ rejectedToken: opts?.rejectedToken, restoring: false });
  }

  /** The raw bearer value (the enrolment-only token wins while it exists); the interceptor adds `Bearer `. */
  authHeader(): string | null {
    return this.enrolmentTokenState() ?? this.accessTokenState();
  }

  signIn(tokens: TokenPair, role: PlatformRole, amr: readonly string[]): void {
    this.replaceIdentity();
    this.accessTokenState.set(tokens.accessToken);
    this.expiresAtState.set(tokens.expiresAt);
    this.roleState.set(role);
    this.amrState.set([...amr]);
  }

  setMe(summary: MeSummary | null): void {
    this.meState.set(summary);
  }

  hasFeature(key: string): boolean {
    return (this.meState()?.features ?? []).includes(key);
  }

  /** Holds the enrolment-only token (WR8): `status()` stays out of `'authenticated'`, `/me` and realtime never start. */
  beginEnrolment(token: string): void {
    this.replaceIdentity();
    this.enrolmentTokenState.set(token);
  }

  /** The explicit user action: `POST /auth/logout`, then end the session locally even if the call failed. */
  async logout(): Promise<void> {
    const generation = this.generationState();
    try {
      await this.api.logout();
    } finally {
      if (generation === this.generationState()) this.signOut('logout');
    }
  }

  /**
   * Ends the session: clears the identity, resets every `SESSION_SCOPED` store once (a throwing one does not stop
   * the rest) and goes to `/login` (with `returnUrl` unless the user logged out). A no-op when already ended.
   */
  signOut(reason: SessionEndReason): void {
    this.endSession(reason, true);
  }

  /** `navigate` false: restore ends the session in place; the guards redirect to `/login` with the sanitized returnUrl. */
  private endSession(reason: SessionEndReason, navigate: boolean): void {
    if (this.endReasonState() !== null) return;
    this.replaceIdentity();
    this.endReasonState.set(reason);
    this.resetScopes();
    if (!navigate) return;
    // Resolved here, never with `inject()`: signOut runs from async callbacks (NG0203).
    const router = this.injector.get(Router);
    const path = router.url.split(/[?#]/)[0];
    if (path === '/login') return;
    void router.navigate(['/login'], reason === 'logout' ? {} : { queryParams: { returnUrl: sanitizeReturnUrl(router.url) } });
  }

  /** Back to a clean anonymous state without ending reason or navigation (`SessionScoped`). */
  reset(): void {
    this.replaceIdentity();
    this.endReasonState.set(null);
  }

  private isStale(token: string, rejectedToken: string | undefined): boolean {
    if (rejectedToken !== undefined && rejectedToken === token) return true;
    return (this.expiresAtState() ?? 0) - Date.now() <= FRESHNESS_MARGIN_MS;
  }

  /** A fresh current token, else `null` (what a flight made stale by a newer session hands its callers). */
  private currentFreshToken(): string | null {
    const token = this.accessTokenState();
    return token !== null && !this.isStale(token, undefined) ? token : null;
  }

  /** Bumps the generation, drops every credential and detaches in-flight work so it can never commit. */
  private replaceIdentity(): void {
    this.generationState.update((n) => n + 1);
    this.accessTokenState.set(null);
    this.expiresAtState.set(null);
    this.roleState.set(null);
    this.amrState.set([]);
    this.meState.set(null);
    this.enrolmentTokenState.set(null);
    this.endReasonState.set(null);
    this.refreshInFlight = null;
    this.refreshingState.set(false);
    // A session created or ended on purpose has nothing left to restore.
    this.restorePromise ??= Promise.resolve();
    this.restoreSettledState.set(true);
  }

  private resetScopes(): void {
    for (const scope of this.scopes) {
      try {
        scope.reset();
      } catch (error) {
        if (isDevMode()) console.warn('session scope reset failed', error);
      }
    }
  }

  private async runRestore(): Promise<void> {
    this.restoreStartedState.set(true);
    try {
      await this.startFlight({ rejectedToken: undefined, restoring: true });
    } catch {
      // Anonymous: no error banner, no navigation (the guards redirect).
    } finally {
      this.restoreSettledState.set(true);
    }
  }

  /** Single flight: concurrent callers share one Promise; the finally clears only the flight that is its own. */
  private startFlight(options: FlightOptions): Promise<string | null> {
    if (this.refreshInFlight !== null) return this.refreshInFlight;
    const generation = this.generationState();
    const flight: Promise<string | null> = this.lock.run(() => this.refreshInsideLock(generation, options)).finally(() => {
      if (this.refreshInFlight === flight) {
        this.refreshInFlight = null;
        this.refreshingState.set(false);
      }
    });
    this.refreshInFlight = flight;
    this.refreshingState.set(true);
    return flight;
  }

  private async refreshInsideLock(generation: number, options: FlightOptions): Promise<string | null> {
    // Another flight or tab may have refreshed (or the session changed) while this one waited for the lock.
    if (this.generationState() !== generation) return this.currentFreshToken();
    const current = this.accessTokenState();
    if (current !== null && !this.isStale(current, options.rejectedToken)) return current;

    let tokens: TokenPair;
    try {
      tokens = await this.api.refresh();
    } catch (cause) {
      return this.onRefreshFailure(cause, generation, options);
    }
    if (this.generationState() !== generation) return this.currentFreshToken();

    const claims = readClaims(tokens.accessToken);
    if (claims === null) {
      // An unreadable token is unusable: leave the user anonymous (not ended) and let the caller see a failure.
      this.accessTokenState.set(null);
      this.expiresAtState.set(null);
      this.roleState.set(null);
      this.amrState.set([]);
      return this.fail(this.mapper.from(new Error('unreadable access token')));
    }
    this.accessTokenState.set(tokens.accessToken);
    this.expiresAtState.set(tokens.expiresAt);
    this.roleState.set(claims.role);
    this.amrState.set(claims.amr);
    this.endReasonState.set(null);
    return tokens.accessToken;
  }

  private onRefreshFailure(cause: unknown, generation: number, options: FlightOptions): string | null {
    // A failure of an older session must not touch the current one.
    if (this.generationState() !== generation) return this.currentFreshToken();
    const error = isAppError(cause) ? cause : this.mapper.from(cause);
    if (options.restoring) this.endRestore(error);
    else if (error.code === 'refresh_token_reused') this.signOut('reused');
    else if (error.code === 'session_revoked') this.signOut('revoked');
    else if (error.code === 'forbidden' && isDevMode()) console.warn('session refresh refused by the origin check (403)');
    return this.fail(error);
  }

  /**
   * Boot restore failure policy (README D6). The SPA cannot see the HttpOnly cookie, so a 401 — missing cookie
   * (`session_revoked`, WT21), expired family, unknown code — is indistinguishable from the anonymous denial and
   * settles anonymous: no banner, no scope reset, no navigation. Only `refresh_token_reused` is terminal
   * (`reused`). Everything else (network, timeout, 5xx, 403, unknown) ends the session in place as
   * `restore_failed`; a later user action may retry (failure table L288).
   */
  private endRestore(error: AppError): void {
    const terminal = error.code === 'refresh_token_reused';
    const anonymous = error.status === 401 && !terminal;
    // A retry from `restore_failed` re-settles: leave that state when the new outcome differs; a repeated transient
    // failure keeps it (and does not reset the scopes twice).
    if (this.endReasonState() === 'restore_failed' && (anonymous || terminal)) this.endReasonState.set(null);
    if (anonymous) return;
    if (terminal) this.endSession('reused', false);
    else {
      if (error.code === 'forbidden' && isDevMode()) console.warn('session refresh refused by the origin check (403)');
      this.endSession('restore_failed', false);
    }
  }

  private fail(error: AppError): never {
    // eslint-disable-next-line @typescript-eslint/only-throw-error -- the refresh contract rejects with an AppError
    throw error;
  }
}
