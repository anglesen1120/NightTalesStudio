import { DOCUMENT } from '@angular/common';
import {
  HttpContext,
  HttpErrorResponse,
  HttpHeaders,
  type HttpEvent,
  type HttpInterceptorFn,
  type HttpRequest,
} from '@angular/common/http';
import { inject } from '@angular/core';
import { type Observable, catchError, from, switchMap, throwError } from 'rxjs';
import { AppErrorMapper, type AppError } from '@core/errors';
import { RETRIED, SILENT_REQUEST, USER_ACTION } from '../http/http-context-tokens';
import { SessionStore } from './session-store';

const API_PREFIX = '/api/v1/';

/**
 * Routes that never carry an automatic `Bearer` header and are never refreshed on or replayed (WR9). Matched on the
 * decoded, normalised pathname — never on a substring — so `/api/v1/auth/refresh-evil` or a query string cannot spoof
 * an entry. `logout` needs the access token too (`LogoutCommand` is not anonymous), but `SessionStore.logout` obtains
 * it first and `HttpSessionRepository.logout` sets the `Authorization` header itself; it passes through untouched.
 */
const AUTH_ROUTES: readonly string[] = [
  '/api/v1/auth/login',
  '/api/v1/auth/login/mfa',
  '/api/v1/auth/refresh',
  '/api/v1/auth/forgot-password',
  '/api/v1/auth/reset-password',
  '/api/v1/auth/logout',
];

/** 401 codes that reach the caller unchanged and are never replayed (WR9). */
const PASS_THROUGH_CODES: readonly string[] = [
  'reauth_required',
  'invalid_credentials',
  'invalid_mfa_code',
  'mfa_token_invalid',
  'mfa_enrolment_required',
  'hub_ticket_invalid',
  'refresh_token_reused',
  'session_revoked',
];

/** Body codes of the 401 an expired access token carries (`09.1.4-T1:21`); an empty code is retryable too. */
const RETRYABLE_CODES: readonly string[] = ['unauthorized', 'unauthenticated'];

const INVALID_TOKEN_CHALLENGE = /\berror\s*=\s*"?invalid_token"?/i;

type RequestKind = 'foreign' | 'auth' | 'api';

function classify(req: HttpRequest<unknown>, baseUrl: string): RequestKind {
  let url: URL;
  try {
    url = new URL(req.url, baseUrl);
  } catch {
    return 'foreign';
  }
  if (url.origin !== new URL(baseUrl).origin || !url.pathname.startsWith(API_PREFIX)) return 'foreign';
  let path: string;
  try {
    // The server decodes `%72efresh` to `refresh` before routing, so the decoded form decides.
    path = decodeURIComponent(url.pathname);
  } catch {
    return 'auth';
  }
  path = path.toLowerCase().replace(/\/+$/, '');
  return AUTH_ROUTES.includes(path) ? 'auth' : 'api';
}

/** `Authorization` value; tolerates a store that already returns the full `Bearer …` form. */
function bearer(token: string): string {
  return token.startsWith('Bearer ') ? token : `Bearer ${token}`;
}

function copyContext(source: HttpContext): HttpContext {
  const copy = new HttpContext();
  for (const key of source.keys()) copy.set(key, source.get(key));
  return copy;
}

/** What a refresh rejection carries: the `AppError` the session API built. Anything else is an unexpected bug. */
function readRefreshFailure(failure: unknown): AppError | null {
  if (typeof failure !== 'object' || failure === null) return null;
  const { code, status } = failure as { code?: unknown; status?: unknown };
  return typeof code === 'string' && typeof status === 'number' ? (failure as AppError) : null;
}

/**
 * The HTTP error the caller's `AppErrorMapper.from` maps back to the same `code`, `retryAfterSec` and `traceId` as the
 * refresh failure. Only those safe members travel: never a token or any other body member.
 */
function rebuildFailure(refresh: AppError, url: string): HttpErrorResponse {
  const traceId = refresh.traceId;
  return new HttpErrorResponse({
    status: refresh.status,
    url,
    headers: refresh.retryAfterSec === undefined ? undefined : new HttpHeaders({ 'Retry-After': String(refresh.retryAfterSec) }),
    error: traceId === undefined ? { code: refresh.code } : { code: refresh.code, traceId },
  });
}

/**
 * Adds the `Bearer` header, refreshes the session at most once per request and replays a request at most once.
 * Everything is injected synchronously at entry: `inject()` is invalid after the first `await`/callback (NG0203).
 * It logs nothing.
 */
export const sessionInterceptor: HttpInterceptorFn = (req, next) => {
  const store = inject(SessionStore);
  const mapper = inject(AppErrorMapper);
  const baseUrl = inject(DOCUMENT).location?.href ?? 'http://localhost/';

  if (classify(req, baseUrl) !== 'api') return next(req);

  const userAction = req.context.get(USER_ACTION);

  /**
   * `bound` is the session generation the request belongs to. The header is attached only while it is still the
   * current one (nothing of session B ever rides on a request of session A), and every response-side effect checks it
   * again in `onResponseError`.
   */
  const send = (request: HttpRequest<unknown>, token: string | null, bound: number): Observable<HttpEvent<unknown>> => {
    // A `null` token is an idle-gated or absent access token: the request goes out bare (task Design, step 2); only
    // the enrolment-only token is presented in that case.
    const value = store.generation() === bound ? (token ?? store.enrolmentToken()) : null;
    const outgoing = value === null ? request : request.clone({ setHeaders: { Authorization: bearer(value) } });
    return next(outgoing).pipe(catchError((error: unknown) => onResponseError(outgoing, error, bound)));
  };

  /** A refresh that was attempted and failed: decide what the caller sees and whether the session ends. */
  const onRefreshFailure = (failure: unknown, original: HttpErrorResponse | null, bound: number): Observable<never> => {
    const refresh = readRefreshFailure(failure);
    if (refresh === null) return throwError(() => failure);
    if (refresh.code === 'network_error' || refresh.kind === 'network') {
      return throwError(() => new HttpErrorResponse({ status: 0, url: req.url }));
    }
    if (refresh.code === 'forbidden' || refresh.status === 403) {
      // Origin misconfiguration (§2): not a session end. `unknown_error` for the caller (a non-HTTP error).
      return throwError(() => new Error('refresh_forbidden'));
    }
    if (refresh.status === 401) {
      // The store has already ended the session for `session_revoked`/`refresh_token_reused`, and a failed restore
      // retry left it anonymous: only a session that is still held in full, and still the request's own, ends here.
      if (store.generation() === bound && store.accessToken() !== null && store.endReason() === null) {
        store.signOut('revoked');
      }
      return throwError(() => original ?? rebuildFailure(refresh, req.url));
    }
    // No HTTP status (an unreadable token the store refused): not a network failure, the caller sees `unknown_error`.
    if (refresh.status === 0) return throwError(() => new Error(refresh.code));
    return throwError(() => rebuildFailure(refresh, req.url));
  };

  const onResponseError = (request: HttpRequest<unknown>, error: unknown, bound: number): Observable<HttpEvent<unknown>> => {
    if (!(error instanceof HttpErrorResponse) || error.status !== 401) return throwError(() => error);
    // The session this request belonged to is gone: its late 401 neither signs out nor replays under the new one.
    if (store.generation() !== bound) return throwError(() => error);

    const code = mapper.from(error).code;
    if (PASS_THROUGH_CODES.includes(code)) {
      if (code === 'session_revoked') store.signOut('revoked');
      else if (code === 'refresh_token_reused') store.signOut('reused');
      return throwError(() => error);
    }

    const retryable =
      RETRYABLE_CODES.includes(code) || INVALID_TOKEN_CHALLENGE.test(error.headers.get('WWW-Authenticate') ?? '');
    if (!retryable || request.context.get(SILENT_REQUEST)) return throwError(() => error);

    if (request.context.get(RETRIED)) {
      // The replay was rejected too: the session is dead (3c).
      store.signOut('revoked');
      return throwError(() => error);
    }

    const rejectedToken = request.headers.get('Authorization')?.replace(/^Bearer /, '') ?? undefined;
    return from(
      store.ensureFreshAccessToken(
        rejectedToken === undefined ? { userAction, generation: bound } : { userAction, rejectedToken, generation: bound },
      ),
    ).pipe(
      catchError((failure: unknown) => onRefreshFailure(failure, error, bound)),
      switchMap((fresh) => {
        // Idle-gated, nothing to refresh (3b) or the session changed meanwhile: keep the session, surface the 401.
        if (fresh === null || store.generation() !== bound) return throwError(() => error);
        const context = copyContext(request.context);
        context.set(RETRIED, true);
        return send(request.clone({ context }), fresh, bound);
      }),
    );
  };

  const entryGeneration = store.generation();
  return from(store.ensureFreshAccessToken({ userAction, generation: entryGeneration })).pipe(
    catchError((failure: unknown) => onRefreshFailure(failure, null, entryGeneration)),
    switchMap((token) => send(req, token, entryGeneration)),
  );
};
