import { HttpClient, HttpHeaders } from '@angular/common/http';
import { Injectable, inject, isDevMode } from '@angular/core';
import { firstValueFrom } from 'rxjs';
import { AppErrorMapper, type AppError } from '@core/errors';
import { silently } from '@core/http';
import { ApiConfiguration, AuthEndpointsClient, apiV1AuthLogoutPost } from '@shared/api';
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
  private readonly http = inject(HttpClient);
  private readonly config = inject(ApiConfiguration);
  private readonly mapper = inject(AppErrorMapper);

  override async refresh(): Promise<TokenPair> {
    try {
      const response = await firstValueFrom(this.client.apiV1AuthRefreshPost(undefined, silently()));
      return { accessToken: response.accessToken, expiresAt: parseExpiresAt(response.expiresAt) };
    } catch (cause) {
      return this.fail(cause);
    }
  }

  /**
   * `POST /auth/logout` needs the access token as `Bearer` next to the cookie (`LogoutCommand` is not anonymous: a
   * Bearer-less call revokes nothing and answers 401 `unauthorized`). The generated client has no header parameter,
   * so the request is built from its `PATH` with an explicit `Authorization`. Resolves on 204 and on 401
   * `session_revoked` (the family is gone). Without a token the POST still goes out (it clears the browser's cookie
   * copy) and every outcome resolves. Any other failure with a token rejects with its `AppError`.
   */
  override async logout(accessToken: string | null): Promise<void> {
    const headers = accessToken === null ? new HttpHeaders() : new HttpHeaders({ Authorization: `Bearer ${accessToken}` });
    try {
      await firstValueFrom(
        this.http.post(`${this.config.rootUrl}${apiV1AuthLogoutPost.PATH}`, null, {
          headers,
          context: silently(),
          responseType: 'text',
        }),
        { defaultValue: undefined },
      );
    } catch (cause) {
      if (accessToken === null) return;
      const error = this.mapper.from(cause);
      if (error.status === 401 && error.code === 'session_revoked') return;
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
