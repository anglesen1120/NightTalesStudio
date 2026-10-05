import { Injectable, inject } from '@angular/core';
import { firstValueFrom } from 'rxjs';
import { AppErrorMapper, type AppError, type Result } from '@core/errors';
import { MeEndpointsClient } from '@shared/api';
import { MfaRepository } from '../application/mfa.repository';
import type { MfaSetup, RecoveryCodes } from '../domain/mfa.model';

/** A recovery-code set is exactly this long (09.10.4-T1); anything else is a broken response, not a partial success. */
const RECOVERY_CODE_COUNT = 10;

/**
 * HTTP adapter of the MFA port over the generated `MeEndpointsClient` (the only importer of `@shared/api` for these
 * routes, D5). The calls are plain (not `silently()`): an expired access token still refreshes through the session
 * interceptor, while `reauth_required`, `invalid_mfa_code` and `concurrency_conflict` reach the caller unchanged
 * (WR9) and the enrolment-only token is presented by the interceptor. No `Idempotency-Key`. The secret, the URI and
 * the codes are returned to the caller and never logged here.
 */
@Injectable()
export class MfaApiRepository extends MfaRepository {
  private readonly client = inject(MeEndpointsClient);
  private readonly mapper = inject(AppErrorMapper);

  override async setup(): Promise<Result<MfaSetup>> {
    try {
      const response = await firstValueFrom(this.client.apiV1MeMfaSetupPost());
      if (!nonEmpty(response.secret) || !nonEmpty(response.otpauthUri)) return { ok: false, error: this.mapper.from(undefined) };
      return { ok: true, value: { secret: response.secret, otpauthUri: response.otpauthUri } };
    } catch (cause) {
      return this.failure(cause);
    }
  }

  override async verify(code: string): Promise<Result<RecoveryCodes>> {
    try {
      const response = await firstValueFrom(this.client.apiV1MeMfaVerifyPost({ body: { code } }));
      return this.codes(response.recoveryCodes);
    } catch (cause) {
      return this.failure(cause);
    }
  }

  override async regenerateRecoveryCodes(): Promise<Result<RecoveryCodes>> {
    try {
      const response = await firstValueFrom(this.client.apiV1MeMfaRecoveryCodesPost());
      return this.codes(response.recoveryCodes);
    } catch (cause) {
      return this.failure(cause);
    }
  }

  private codes(raw: unknown): Result<RecoveryCodes> {
    if (!Array.isArray(raw) || raw.length !== RECOVERY_CODE_COUNT || !raw.every(nonEmpty)) return { ok: false, error: this.mapper.from(undefined) };
    return { ok: true, value: [...(raw as readonly string[])] };
  }

  private failure(cause: unknown): Result<never> {
    return { ok: false, error: isAppError(cause) ? cause : this.mapper.from(cause) };
  }
}

function nonEmpty(value: unknown): value is string {
  return typeof value === 'string' && value !== '';
}

function isAppError(value: unknown): value is AppError {
  return typeof value === 'object' && value !== null && 'code' in value && 'status' in value && 'kind' in value;
}
