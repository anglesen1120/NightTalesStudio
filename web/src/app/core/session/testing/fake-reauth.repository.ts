import type { AppError, Result } from '@core/errors';
import { ReauthPort } from '../reauth.port';

export type FakeReauthResult = Result<{ reauthenticatedAt: string }>;

export const FAKE_REAUTHENTICATED_AT = '2026-10-05T10:00:00.000+00:00';

export function fakeReauthOk(): FakeReauthResult {
  return { ok: true, value: { reauthenticatedAt: FAKE_REAUTHENTICATED_AT } };
}

export function fakeReauthError(code: string, status: number, kind: AppError['kind'], retryAfterSec?: number): FakeReauthResult {
  const error: AppError = retryAfterSec === undefined ? { code, status, kind, fieldErrors: {} } : { code, status, kind, fieldErrors: {}, retryAfterSec };
  return { ok: false, error };
}

/**
 * Hand-written scriptable `ReauthPort` (README D15: no mocking library). `calls` records every attempt (the specs
 * assert the secrets only here). `enqueue` scripts the next answers in order, a pending `Promise` holds one open;
 * with an empty queue the call succeeds.
 */
export class FakeReauthRepository extends ReauthPort {
  readonly calls: { readonly password: string; readonly code: string }[] = [];
  private readonly queue: (FakeReauthResult | Promise<FakeReauthResult>)[] = [];

  enqueue(...answers: (FakeReauthResult | Promise<FakeReauthResult>)[]): void {
    this.queue.push(...answers);
  }

  override reauth(password: string, code: string): Promise<FakeReauthResult> {
    this.calls.push({ password, code });
    return Promise.resolve(this.queue.shift() ?? fakeReauthOk());
  }
}
