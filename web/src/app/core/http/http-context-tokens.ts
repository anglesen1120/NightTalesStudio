import type { IdempotencyIntent } from './idempotency-keys';
import { HttpContextToken } from '@angular/common/http';

/** A background request: its 401 is never refreshed on (the caller owns the failure). Set through `silently()`. */
export const SILENT_REQUEST = new HttpContextToken<boolean>(() => false);

/** A request made on behalf of a user gesture: it may refresh the session even while the tab is idle (WR11). */
export const USER_ACTION = new HttpContextToken<boolean>(() => false);

/** Set by the session interceptor on the one replay of a request; a replay is never replayed (WR9). */
export const RETRIED = new HttpContextToken<boolean>(() => false);

/** The user intent whose `Idempotency-Key` the request carries; set through `withIdempotency()`, read by `idempotencyInterceptor`. */
export const IDEMPOTENCY_INTENT = new HttpContextToken<IdempotencyIntent | null>(() => null);
