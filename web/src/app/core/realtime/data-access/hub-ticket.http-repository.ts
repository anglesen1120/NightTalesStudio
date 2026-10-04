import { Injectable, inject } from '@angular/core';
import { firstValueFrom } from 'rxjs';
import { AppErrorMapper } from '@core/errors';
import { HubEndpointsClient } from '@shared/api';
import { HubTicketRepository } from '../hub-ticket.repository';
import type { HubTicket } from '../realtime.model';

/** A hub ticket is a short opaque token; anything longer is not one and must never reach a URL. */
const MAX_TICKET_LENGTH = 512;
/** `expiresAt` must be ISO-8601 date-time with a timezone (.NET may emit up to 7 fractional digits); `Date.parse` alone accepts non-ISO text. */
const ISO_DATE_TIME_WITH_TIMEZONE = /^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(\.\d{1,7})?(Z|[+-]\d{2}:\d{2})$/;

/**
 * `POST /api/v1/hubs/ticket` through the generated client (README D5). The request is a plain background call: no
 * `USER_ACTION` flag, so a 401 refreshes the session only while the user is active (WR11). The response body is never
 * logged and the ticket is returned to the caller only. A malformed body (empty/oversized/non-string ticket,
 * unparsable `expiresAt`) rejects with a plain Error that never contains the ticket value.
 */
@Injectable()
export class HubTicketHttpRepository extends HubTicketRepository {
  private readonly client = inject(HubEndpointsClient);
  private readonly mapper = inject(AppErrorMapper);

  override async issue(): Promise<HubTicket> {
    let dto;
    try {
      dto = await firstValueFrom(this.client.apiV1HubsTicketPost());
    } catch (cause) {
      // eslint-disable-next-line @typescript-eslint/only-throw-error -- the HubTicketRepository port rejects with an AppError
      throw this.mapper.from(cause);
    }
    const ticket: unknown = dto.ticket;
    if (typeof ticket !== 'string' || ticket.length === 0 || ticket.length > MAX_TICKET_LENGTH) {
      throw new Error('Hub ticket response is malformed: ticket is missing, empty or too long');
    }
    const expiresAt =
      typeof dto.expiresAt === 'string' && ISO_DATE_TIME_WITH_TIMEZONE.test(dto.expiresAt) ? Date.parse(dto.expiresAt) : Number.NaN;
    if (!Number.isFinite(expiresAt)) throw new Error('Hub ticket response is malformed: expiresAt is not a valid date');
    return { ticket, expiresAt };
  }
}
