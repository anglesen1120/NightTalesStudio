import type { AppError } from '@core/errors';
import { HubTicketRepository } from '../hub-ticket.repository';
import type { HubTicket } from '../realtime.model';

function appError(code: string, status: number, kind: AppError['kind'], retryAfterSec?: number): AppError {
  return { code, status, kind, fieldErrors: {}, ...(retryAfterSec === undefined ? {} : { retryAfterSec }) };
}

interface PendingTicket {
  readonly ticket: HubTicket;
  readonly resolve: (ticket: HubTicket) => void;
  readonly reject: (error: AppError) => void;
}

/** Hand-written scriptable `HubTicketRepository` (README D15). `calls` counts `issue()`; each call gets a new ticket. */
export class FakeHubTicketRepository extends HubTicketRepository {
  calls = 0;
  /** FIFO of failures: each `issue()` consumes one; when empty it succeeds. */
  readonly failures: AppError[] = [];
  /** When true `issue()` waits for `resolveNext`/`rejectNext`. */
  deferred = false;
  private readonly pending: PendingTicket[] = [];

  static unauthorized(): AppError {
    return appError('hub_ticket_invalid', 401, 'auth');
  }

  static rateLimited(retryAfterSec: number): AppError {
    return appError('rate_limited', 429, 'rate_limit', retryAfterSec);
  }

  static unavailable(): AppError {
    return appError('server_error', 503, 'server');
  }

  /** The ticket string number `n` (1-based) the fake hands out. */
  static ticketNumber(n: number): string {
    return `t-${String(n)}-secret`;
  }

  override issue(): Promise<HubTicket> {
    this.calls += 1;
    const failure = this.failures.shift();
    // eslint-disable-next-line @typescript-eslint/prefer-promise-reject-errors -- the port rejects with an AppError by contract
    if (failure !== undefined) return Promise.reject(failure);
    const ticket: HubTicket = { ticket: FakeHubTicketRepository.ticketNumber(this.calls), expiresAt: Date.now() + 60_000 };
    if (!this.deferred) return Promise.resolve(ticket);
    return new Promise<HubTicket>((resolve, reject) => {
      this.pending.push({ ticket, resolve, reject });
    });
  }

  resolveNext(): void {
    const next = this.pending.shift();
    next?.resolve(next.ticket);
  }

  rejectNext(error: AppError): void {
    this.pending.shift()?.reject(error);
  }
}
