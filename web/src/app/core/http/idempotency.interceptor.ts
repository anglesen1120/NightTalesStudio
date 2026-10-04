import type { HttpInterceptorFn } from '@angular/common/http';
import { IDEMPOTENCY_INTENT } from './http-context-tokens';

const IDEMPOTENCY_HEADER = 'Idempotency-Key';
const SAFE_METHODS: readonly string[] = ['GET', 'HEAD', 'OPTIONS'];

/**
 * Stamps `Idempotency-Key: <intent.key>` on a request marked with `withIdempotency(intent)`. Registered BEFORE
 * `sessionInterceptor`, so the 401 replay (a `clone()` that keeps headers and context) carries the same key. A header
 * already present is never overwritten, `GET`/`HEAD`/`OPTIONS` and unmarked requests pass through unchanged. The key is
 * never logged.
 */
export const idempotencyInterceptor: HttpInterceptorFn = (req, next) => {
  const intent = req.context.get(IDEMPOTENCY_INTENT);
  if (intent === null || SAFE_METHODS.includes(req.method.toUpperCase()) || req.headers.has(IDEMPOTENCY_HEADER)) {
    return next(req);
  }
  return next(req.clone({ setHeaders: { [IDEMPOTENCY_HEADER]: intent.key } }));
};
