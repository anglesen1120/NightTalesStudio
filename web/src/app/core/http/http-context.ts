import { HttpContext } from '@angular/common/http';
import { IDEMPOTENCY_INTENT, SILENT_REQUEST, USER_ACTION } from './http-context-tokens';
import type { IdempotencyIntent } from './idempotency-keys';

/** Marks a request as background: the session interceptor never refreshes on its 401. Returns `ctx` (or a new context). */
export function silently(ctx: HttpContext = new HttpContext()): HttpContext {
  return ctx.set(SILENT_REQUEST, true);
}

/** Marks a request as a user action: it refreshes the session even while the tab is idle (WR11). */
export function userAction(ctx: HttpContext = new HttpContext()): HttpContext {
  return ctx.set(USER_ACTION, true);
}

/** Marks a paid/platform POST with its user intent: `idempotencyInterceptor` sends the intent's key. Reuse the same intent on every retry. */
export function withIdempotency(intent: IdempotencyIntent, ctx: HttpContext = new HttpContext()): HttpContext {
  return ctx.set(IDEMPOTENCY_INTENT, intent);
}
