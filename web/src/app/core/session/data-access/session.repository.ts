import { Injectable, inject, isDevMode } from '@angular/core';
import { firstValueFrom } from 'rxjs';
import { AppErrorMapper, type AppError } from '@core/errors';
import { silently } from '@core/http';
import { AuthEndpointsClient } from '@shared/api';
import { SessionApi } from '../session.api';
import type { TokenPair } from '../token-pair';

/**
 * ISO instant → epoch ms. A missing or unparsable value yields `0`, i.e. an already-stale token, so the next request
 * refreshes (task failure table); one dev-console line, never the value.
 */
function parseExpiresAt(raw: unknown): number {
  const parsed = typeof raw === 'string' ? Date.parse(raw) : Number.NaN;
  if (Number.isNaN(parsed)) {
    if (isDevMode()) console.warn('refresh response carried no parsable expiresAt; treating the token as stale');
    return 0;
  }
  return parsed;
}

/**
 * The only file of `core/session` that imports the generated client (README D5). The refresh cookie is `HttpOnly`
 * and same-origin, so the browser attaches it without any `withCredentials` setup. Both requests are `SILENT`:
 * the session interceptor never refreshes on their 401, nor replays them.
 */
@Injectable()
export class HttpSessionRepository extends SessionApi {
  private readonly client = inject(AuthEndpointsClient);
  private readonly mapper = inject(AppErrorMapper);

  override async refresh(): Promise<TokenPair> {
    try {
      const response = await firstValueFrom(this.client.apiV1AuthRefreshPost(undefined, silently()));
      return { accessToken: response.accessToken, expiresAt: parseExpiresAt(response.expiresAt) };
    } catch (cause) {
      return this.fail(cause);
    }
  }

  override async logout(): Promise<void> {
    try {
      await firstValueFrom(this.client.apiV1AuthLogoutPost(undefined, silently()), { defaultValue: undefined });
    } catch (cause) {
      const error = this.mapper.from(cause);
      // 401: the session was already gone, which is what the caller wanted.
      if (error.status === 401) return;
      this.rethrow(error);
    }
  }

  private fail(cause: unknown): never {
    return this.rethrow(this.mapper.from(cause));
  }

  private rethrow(error: AppError): never {
    // eslint-disable-next-line @typescript-eslint/only-throw-error -- the SessionApi port rejects with an AppError
    throw error;
  }
}
