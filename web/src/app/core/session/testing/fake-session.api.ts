import type { AppError } from '@core/errors';
import { SessionApi } from '../session.api';
import { type PlatformRole, type TokenPair } from '../token-pair';

export type FakeSessionMode = 'ok' | '401' | 'network' | 'deferred' | 'forbidden' | 'reused' | 'revoked' | 'server';

function appError(code: string, status: number, kind: AppError['kind']): AppError {
  return { code, status, kind, fieldErrors: {} };
}

const FAILURES: Readonly<Record<Exclude<FakeSessionMode, 'ok' | 'deferred'>, AppError>> = {
  '401': appError('unauthorized', 401, 'auth'),
  network: appError('network_error', 0, 'network'),
  forbidden: appError('forbidden', 403, 'auth'),
  reused: appError('refresh_token_reused', 401, 'auth'),
  revoked: appError('session_revoked', 401, 'auth'),
  server: appError('server_error', 503, 'server'),
};

function base64Url(text: string): string {
  return btoa(text).replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '');
}

/** An unsigned, JWT-shaped token whose payload carries the `role` and `amr` claims the store decodes. */
export function fakeJwt(role: PlatformRole, amr: readonly string[] = ['pwd']): string {
  const header = base64Url(JSON.stringify({ alg: 'none', typ: 'JWT' }));
  const payload = base64Url(JSON.stringify({ sub: 'user-1', role, amr: amr.length === 1 ? amr[0] : amr }));
  return `${header}.${payload}.sig`;
}

interface Deferred {
  readonly resolve: (tokens: TokenPair) => void;
  readonly reject: (error: AppError) => void;
}

/**
 * Hand-written scriptable `SessionApi` (README D15: no mocking library). `calls` counts `refresh()` invocations,
 * `logoutCalls` the `logout()` ones. In `deferred` mode `refresh()` stays pending until `resolveNext`/`rejectNext`.
 */
export class FakeSessionApi extends SessionApi {
  mode: FakeSessionMode = 'ok';
  calls = 0;
  logoutCalls = 0;
  /** Returned by `refresh()` in `ok` mode. */
  next: TokenPair = { accessToken: fakeJwt('user'), expiresAt: Date.now() + 15 * 60_000 };
  /** When set, `logout()` rejects with it. */
  logoutFailure: AppError | null = null;

  private readonly pending: Deferred[] = [];
  private readonly early: (() => void)[] = [];

  override refresh(): Promise<TokenPair> {
    this.calls += 1;
    if (this.mode === 'ok') return Promise.resolve(this.next);
    if (this.mode === 'deferred') {
      return new Promise<TokenPair>((resolve, reject) => {
        this.pending.push({ resolve, reject });
        this.early.shift()?.();
      });
    }
    // eslint-disable-next-line @typescript-eslint/prefer-promise-reject-errors -- the port rejects with an AppError by contract
    return Promise.reject(FAILURES[this.mode]);
  }

  override logout(): Promise<void> {
    this.logoutCalls += 1;
    // eslint-disable-next-line @typescript-eslint/prefer-promise-reject-errors -- the port rejects with an AppError by contract
    return this.logoutFailure === null ? Promise.resolve() : Promise.reject(this.logoutFailure);
  }

  /** Settles the oldest pending deferred refresh (or the next one to start) with `tokens`. */
  resolveNext(tokens: TokenPair): void {
    this.settleNext((deferred) => deferred.resolve(tokens));
  }

  /** Rejects the oldest pending deferred refresh (or the next one to start) with `error`. */
  rejectNext(error: AppError): void {
    this.settleNext((deferred) => deferred.reject(error));
  }

  private settleNext(settle: (deferred: Deferred) => void): void {
    const deferred = this.pending.shift();
    if (deferred !== undefined) settle(deferred);
    else this.early.push(() => settle(this.pending.shift() as Deferred));
  }
}
