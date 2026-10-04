import { Injectable } from '@angular/core';

/**
 * One user intent (a click or form submit on a paid/platform command) and the `Idempotency-Key` it owns. The same
 * object is reused by every retry of that click (the one 401 replay of the session interceptor and a manual "Retry"
 * after a network error), so the server sees one key and applies one effect; a new click calls
 * `IdempotencyKeys.begin()` again. `settle()` marks the intent finished (success or any non-retryable answer).
 *
 * Consumer contract (10.5.2-T1 and every later paid command): the store calls `begin()` once at the top of the
 * user-intent handler, never inside a repository or a retry; the repository sends `withIdempotency(intent)` together
 * with `userAction()`; the store keeps the open intent next to its error so "Retry" reuses it, drops it on `reset()`,
 * and ignores a late answer whose session/request generation changed. a11y (D16) for the consumer: while an intent is
 * open the triggering control is `disabled` and `aria-busy="true"` with a text/Spinner sibling (never colour alone),
 * failure renders `FormErrorAlert` with focus kept on the retried control, and a double click must not announce twice.
 * The key is never logged and never persisted.
 */
export class IdempotencyIntent {
  private settled = false;

  constructor(
    /** `crypto.randomUUID()`: 36 chars, inside the API's 1–128 printable-ASCII (`0x21–0x7E`) rule. */
    readonly key: string,
    /** Label for the dev console and store tests only (e.g. `episode.generate`); never sent. */
    readonly intent: string,
  ) {}

  get isSettled(): boolean {
    return this.settled;
  }

  /** One-way: a settled intent stays settled. */
  settle(): void {
    this.settled = true;
  }
}

/** Mints keys for user intents. Every `begin()` yields a NEW key; reuse means passing the same `IdempotencyIntent`. */
@Injectable({ providedIn: 'root' })
export class IdempotencyKeys {
  begin(intent: string): IdempotencyIntent {
    return new IdempotencyIntent(this.newKey(), intent);
  }

  newKey(): string {
    return crypto.randomUUID();
  }
}
