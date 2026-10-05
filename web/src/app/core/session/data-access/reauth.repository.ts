import { Injectable, inject } from '@angular/core';
import { firstValueFrom } from 'rxjs';
import { AppErrorMapper, type Result } from '@core/errors';
import { silently } from '@core/http';
import { AuthEndpointsClient } from '@shared/api';
import { ReauthPort } from '../reauth.port';

/**
 * `POST /auth/reauth` over the generated client (README D5: this is the only file of the re-auth slice that imports
 * it). The route is not an interceptor `AUTH_ROUTES` entry, so the session `Bearer` is attached; the request is
 * `SILENT`, so a 401 never triggers a refresh and the password POST is never replayed. 401 `session_revoked` still ends
 * the session in the interceptor (`PASS_THROUGH_CODES`). No idempotency key: the route is not one of its callers.
 * Failures resolve as `{ ok: false }` with the mapped `AppError`; nothing is logged.
 */
@Injectable()
export class ReauthApiRepository extends ReauthPort {
  private readonly client = inject(AuthEndpointsClient);
  private readonly mapper = inject(AppErrorMapper);

  override async reauth(password: string, code: string): Promise<Result<{ reauthenticatedAt: string }>> {
    try {
      const response = await firstValueFrom(
        this.client.apiV1AuthReauthPost({ body: { password, code: code === '' ? null : code } }, silently()),
      );
      return { ok: true, value: { reauthenticatedAt: response.reauthenticatedAt } };
    } catch (cause) {
      return { ok: false, error: this.mapper.from(cause) };
    }
  }
}
