import type { HubTicket } from './realtime.model';

/**
 * The hub-ticket port (README D5). `issue()` rejects with an `AppError` (built by `AppErrorMapper`): `401` when the
 * access token is refused, `429` + `retryAfterSec` on the `hub` policy, `5xx`/network otherwise. The resolved ticket
 * is single-use and 60 s long: callers keep it in a local variable.
 */
export abstract class HubTicketRepository {
  abstract issue(): Promise<HubTicket>;
}
