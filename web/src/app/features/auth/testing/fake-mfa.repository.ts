import type { AppError, Result } from '@core/errors';
import { MfaRepository } from '../application/mfa.repository';
import type { MfaSetup, RecoveryCodes } from '../domain/mfa.model';

export type MfaMethod = 'setup' | 'verify' | 'regenerateRecoveryCodes';

interface Pending {
  readonly method: MfaMethod;
  readonly resolve: (result: Result<unknown>) => void;
}

export const FAKE_SETUP: MfaSetup = {
  secret: 'JBSWY3DPEHPK3PXP',
  otpauthUri: 'otpauth://totp/NightTales:owner@example.com?secret=JBSWY3DPEHPK3PXP&issuer=NightTales',
};

export function fakeRecoveryCodes(prefix = 'code'): RecoveryCodes {
  return Array.from({ length: 10 }, (_, i) => `${prefix}-${i + 1}`);
}

/**
 * Hand-written scriptable `MfaRepository` (README D15: no mocking library). Scripted answers are queued per method
 * (`willReturn`/`willFail` append; the last one repeats); defaults are a valid setup and 10 codes. In deferred mode a
 * call stays pending until `resolveNext`.
 */
export class FakeMfaRepository extends MfaRepository {
  readonly calls: Array<{ method: MfaMethod; args: unknown[] }> = [];
  private readonly scripted: Record<MfaMethod, Array<Result<unknown>>> = { setup: [], verify: [], regenerateRecoveryCodes: [] };
  private readonly pending: Pending[] = [];
  private readonly repeating = new Set<MfaMethod>();
  private deferred = false;

  willReturn(method: 'setup', result: Result<MfaSetup>): this;
  willReturn(method: 'verify' | 'regenerateRecoveryCodes', result: Result<RecoveryCodes>): this;
  willReturn(method: MfaMethod, result: Result<unknown>): this {
    this.enqueue(method, result);
    return this;
  }

  willFail(method: MfaMethod, error: AppError): this {
    this.enqueue(method, { ok: false, error });
    return this;
  }

  defer(on = true): this {
    this.deferred = on;
    return this;
  }

  get pendingCount(): number {
    return this.pending.length;
  }

  resolveNext(result?: Result<unknown>): void {
    const next = this.pending.shift();
    if (next === undefined) throw new Error('no pending call');
    next.resolve(result ?? this.next(next.method));
  }

  callsOf(method: MfaMethod): number {
    return this.calls.filter((call) => call.method === method).length;
  }

  override setup(): Promise<Result<MfaSetup>> {
    return this.answer('setup', []) as Promise<Result<MfaSetup>>;
  }

  override verify(code: string): Promise<Result<RecoveryCodes>> {
    return this.answer('verify', [code]) as Promise<Result<RecoveryCodes>>;
  }

  override regenerateRecoveryCodes(): Promise<Result<RecoveryCodes>> {
    return this.answer('regenerateRecoveryCodes', []) as Promise<Result<RecoveryCodes>>;
  }

  /** A sole answer that was already served is only repeating: a new answer replaces it instead of queueing behind it. */
  private enqueue(method: MfaMethod, result: Result<unknown>): void {
    const queue = this.scripted[method];
    if (this.repeating.delete(method) && queue.length === 1) queue.length = 0;
    queue.push(result);
  }

  private next(method: MfaMethod): Result<unknown> {
    const queue = this.scripted[method];
    const scripted = queue.length > 1 ? queue.shift() : queue[0];
    if (queue.length === 1) this.repeating.add(method);
    if (scripted !== undefined) return scripted;
    return { ok: true, value: method === 'setup' ? FAKE_SETUP : fakeRecoveryCodes() };
  }

  private answer(method: MfaMethod, args: unknown[]): Promise<Result<unknown>> {
    this.calls.push({ method, args });
    if (this.deferred) {
      return new Promise<Result<unknown>>((resolve) => {
        this.pending.push({ method, resolve });
      });
    }
    return Promise.resolve(this.next(method));
  }
}
