import type { AppError, Result } from '@core/errors';
import { AuthRepository } from '../application/auth.repository';
import type { LoginOutcome } from '../domain/auth.model';
import type { MfaCodeMode } from '../domain/mfa.model';

export type AuthMethod = 'login' | 'loginMfa' | 'forgotPassword' | 'resetPassword' | 'logout';

interface Pending {
  readonly method: AuthMethod;
  readonly resolve: (result: Result<unknown>) => void;
}

const OK_VOID: Result<void> = { ok: true, value: undefined };

/**
 * Hand-written scriptable `AuthRepository` (README D15: no mocking library). Each method answers its scripted result
 * (`willReturn`/`willFail`; default: success). In deferred mode a call stays pending until `resolveNext`.
 */
export class FakeAuthRepository extends AuthRepository {
  readonly calls: Array<{ method: string; args: unknown[] }> = [];
  private readonly scripted = new Map<AuthMethod, Result<unknown>>();
  private readonly pending: Pending[] = [];
  private deferred = false;

  willReturn(method: 'login' | 'loginMfa', result: Result<LoginOutcome>): this;
  willReturn(method: Exclude<AuthMethod, 'login' | 'loginMfa'>, result: Result<void>): this;
  willReturn(method: AuthMethod, result: Result<unknown>): this {
    this.scripted.set(method, result);
    return this;
  }

  willFail(method: AuthMethod, error: AppError): this {
    this.scripted.set(method, { ok: false, error });
    return this;
  }

  /** Subsequent calls stay pending until `resolveNext`. */
  defer(on = true): this {
    this.deferred = on;
    return this;
  }

  get pendingCount(): number {
    return this.pending.length;
  }

  /** Resolves the oldest pending call with its scripted result, or with `result` when given. */
  resolveNext(result?: Result<unknown>): void {
    const next = this.pending.shift();
    if (next === undefined) throw new Error('no pending call');
    next.resolve(result ?? this.scripted.get(next.method) ?? this.fallback(next.method));
  }

  override login(email: string, password: string): Promise<Result<LoginOutcome>> {
    return this.answer('login', [email, password]) as Promise<Result<LoginOutcome>>;
  }

  override forgotPassword(email: string): Promise<Result<void>> {
    return this.answer('forgotPassword', [email]) as Promise<Result<void>>;
  }

  override resetPassword(token: string, password: string): Promise<Result<void>> {
    return this.answer('resetPassword', [token, password]) as Promise<Result<void>>;
  }

  override loginMfa(mfaToken: string, code: string, mode: MfaCodeMode): Promise<Result<LoginOutcome>> {
    return this.answer('loginMfa', [mfaToken, code, mode]) as Promise<Result<LoginOutcome>>;
  }

  override logout(): Promise<Result<void>> {
    return this.answer('logout', []) as Promise<Result<void>>;
  }

  private fallback(method: AuthMethod): Result<unknown> {
    if (method === 'login' || method === 'loginMfa') throw new Error(`FakeAuthRepository.${method} needs willReturn/willFail`);
    return OK_VOID;
  }

  private answer(method: AuthMethod, args: unknown[]): Promise<Result<unknown>> {
    this.calls.push({ method, args });
    if (this.deferred) {
      return new Promise<Result<unknown>>((resolve) => {
        this.pending.push({ method, resolve });
      });
    }
    return Promise.resolve(this.scripted.get(method) ?? this.fallback(method));
  }
}
