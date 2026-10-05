import { HttpErrorResponse } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { firstValueFrom } from 'rxjs';
import { AppErrorMapper, type AppError, type Result } from '@core/errors';
import { silently } from '@core/http';
import { SessionStore } from '@core/session';
import { AuthEndpointsClient, type LoginResponse } from '@shared/api';
import { AuthRepository } from '../application/auth.repository';
import type { AuthRole, LoginOutcome, Session } from '../domain/auth.model';

/**
 * HTTP adapter of the auth port; the only file of `features/auth` that imports the generated client (D5).
 *
 * Login is the one call whose 401 carries data. `mfa_required` and `mfa_enrolment_required` are not failures of the
 * user's input but the next step, so they resolve `ok` instead of `error`:
 * - `401 mfa_required { mfaToken }`              → `{ kind: 'mfa', mfaToken }`;
 * - `401 mfa_enrolment_required { token }`        → `{ kind: 'enrolment', token }` (body key `token` per plan §10 OPEN
 *   [INFERENCE]; `mfaToken` is accepted too). `AppErrorMapper` only keeps `mfaToken`, so the enrolment token is read
 *   from the raw response body here.
 * A 401 without a usable token stays an error. Every auth request is `silently()` (the session interceptor must not
 * refresh on a login 401) and carries no `Idempotency-Key`. Nothing is logged: bodies hold credentials and tokens.
 */
@Injectable()
export class AuthApiRepository extends AuthRepository {
  private readonly client = inject(AuthEndpointsClient);
  private readonly mapper = inject(AppErrorMapper);
  private readonly session = inject(SessionStore);

  override async login(email: string, password: string): Promise<Result<LoginOutcome>> {
    try {
      const response = await firstValueFrom(this.client.apiV1AuthLoginPost({ body: { email, password } }, silently()));
      const session = toSession(response);
      return session === null ? { ok: false, error: this.mapper.from(undefined) } : { ok: true, value: { kind: 'ok', session } };
    } catch (cause) {
      const error = this.mapper.from(cause);
      const challenge = error.status === 401 ? this.challenge(error, cause) : null;
      return challenge === null ? { ok: false, error } : { ok: true, value: challenge };
    }
  }

  override async forgotPassword(email: string): Promise<Result<void>> {
    return this.void(() => firstValueFrom(this.client.apiV1AuthForgotPasswordPost({ body: { email } }, silently()), { defaultValue: undefined }));
  }

  override async resetPassword(token: string, password: string): Promise<Result<void>> {
    return this.void(() =>
      firstValueFrom(this.client.apiV1AuthResetPasswordPost({ body: { token, newPassword: password } }, silently()), { defaultValue: undefined }),
    );
  }

  override async logout(): Promise<Result<void>> {
    return this.void(() => this.session.logout());
  }

  private async void(call: () => Promise<unknown>): Promise<Result<void>> {
    try {
      await call();
      return { ok: true, value: undefined };
    } catch (cause) {
      return { ok: false, error: isAppError(cause) ? cause : this.mapper.from(cause) };
    }
  }

  private challenge(error: AppError, cause: unknown): LoginOutcome | null {
    if (error.code === 'mfa_required') {
      const mfaToken = nonEmpty(error.problem?.mfaToken) ?? rawToken(cause);
      return mfaToken === null ? null : { kind: 'mfa', mfaToken };
    }
    if (error.code === 'mfa_enrolment_required') {
      const token = rawToken(cause);
      return token === null ? null : { kind: 'enrolment', token };
    }
    return null;
  }
}

function isAppError(value: unknown): value is AppError {
  return typeof value === 'object' && value !== null && 'code' in value && 'status' in value && 'kind' in value;
}

function nonEmpty(value: unknown): string | null {
  return typeof value === 'string' && value !== '' ? value : null;
}

/** `token` or `mfaToken` of a 401 body; the body may be a parsed object or a JSON string. */
function rawToken(cause: unknown): string | null {
  if (!(cause instanceof HttpErrorResponse)) return null;
  let body: unknown = cause.error;
  if (typeof body === 'string') {
    try {
      body = JSON.parse(body);
    } catch {
      return null;
    }
  }
  if (typeof body !== 'object' || body === null) return null;
  const { token, mfaToken } = body as Record<string, unknown>;
  return nonEmpty(token) ?? nonEmpty(mfaToken);
}

function isRole(value: unknown): value is AuthRole {
  return value === 'user' || value === 'admin' || value === 'owner';
}

/** `null` when the response has no usable role: the caller reports `unknown_error` instead of guessing one. An
 *  unparsable `expiresAt` becomes `0` (already stale: the next request refreshes), as in `core/session`. */
function toSession(response: LoginResponse): Session | null {
  if (!isRole(response.role) || typeof response.accessToken !== 'string' || response.accessToken === '') return null;
  const parsed = Date.parse(response.expiresAt);
  const amr = Array.isArray(response.amr) ? response.amr.filter((m): m is string => typeof m === 'string') : [];
  return { accessToken: response.accessToken, expiresAt: Number.isNaN(parsed) ? 0 : parsed, role: response.role, amr };
}
