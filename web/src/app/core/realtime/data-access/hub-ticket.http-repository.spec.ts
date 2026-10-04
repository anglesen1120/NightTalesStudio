import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { SILENT_REQUEST, USER_ACTION } from '@core/http';
import type { AppError } from '@core/errors';
import { ZONELESS_TEST_PROVIDERS } from '../../../testing/providers';
import { HubTicketHttpRepository } from './hub-ticket.http-repository';

const URL = '/api/v1/hubs/ticket';

function setup(): { repository: HubTicketHttpRepository; http: HttpTestingController } {
  TestBed.configureTestingModule({
    providers: [...ZONELESS_TEST_PROVIDERS, provideHttpClient(), provideHttpClientTesting(), HubTicketHttpRepository],
  });
  return { repository: TestBed.inject(HubTicketHttpRepository), http: TestBed.inject(HttpTestingController) };
}

describe('HubTicketHttpRepository', () => {
  afterEach(() => {
    TestBed.inject(HttpTestingController).verify();
  });

  it('POSTs the ticket route as a background call and maps the ISO expiresAt', async () => {
    const { repository, http } = setup();
    const result = repository.issue();
    const req = http.expectOne(URL);
    expect(req.request.method).toBe('POST');
    expect(req.request.context.get(USER_ACTION)).toBe(false);
    expect(req.request.context.get(SILENT_REQUEST)).toBe(false);
    req.flush({ ticket: 't-1', expiresAt: '2026-10-04T10:01:00.000+00:00' });
    await expect(result).resolves.toEqual({ ticket: 't-1', expiresAt: Date.parse('2026-10-04T10:01:00.000+00:00') });
  });

  async function rejection(body: object): Promise<Error> {
    const { repository, http } = setup();
    const result = repository.issue();
    http.expectOne(URL).flush(body);
    return (await result.then(
      () => null,
      (cause: unknown) => cause,
    )) as Error;
  }

  it('rejects an unparsable expiresAt without leaking the ticket', async () => {
    const error = await rejection({ ticket: 'secret-ticket', expiresAt: 'nope' });
    expect(error).toBeInstanceOf(Error);
    expect(error.message).toContain('expiresAt');
    expect(error.message).not.toContain('secret-ticket');
  });

  it.each([['Oct 4 2026'], ['2026-10-04'], ['2026-10-04T10:01:00'], ['2026-13-45T99:99:99Z']])(
    'rejects the non-ISO or incomplete expiresAt %s without leaking the ticket',
    async (expiresAt) => {
      const error = await rejection({ ticket: 'secret-ticket', expiresAt });
      expect(error).toBeInstanceOf(Error);
      expect(error.message).toContain('expiresAt');
      expect(error.message).not.toContain('secret-ticket');
    },
  );

  it.each([['2026-10-04T10:01:00Z'], ['2026-10-04T10:01:00+02:00'], ['2026-10-04T10:01:00.1234567Z']])(
    'accepts the ISO expiresAt %s',
    async (expiresAt) => {
      const { repository, http } = setup();
      const result = repository.issue();
      http.expectOne(URL).flush({ ticket: 't-1', expiresAt });
      await expect(result).resolves.toEqual({ ticket: 't-1', expiresAt: Date.parse(expiresAt) });
    },
  );

  it.each([
    ['empty', ''],
    ['non-string', 42],
    ['missing', undefined],
    ['oversized', 'x'.repeat(513)],
  ])('rejects a %s ticket', async (_label, ticket) => {
    const error = await rejection({ ticket, expiresAt: '2026-10-04T10:01:00.000+00:00' });
    expect(error).toBeInstanceOf(Error);
    expect(error.message).toContain('ticket');
    expect(error.message).not.toContain('xxxx');
  });

  it('accepts a ticket of exactly the maximum length', async () => {
    const { repository, http } = setup();
    const result = repository.issue();
    http.expectOne(URL).flush({ ticket: 'x'.repeat(512), expiresAt: '2026-10-04T10:01:00.000+00:00' });
    await expect(result).resolves.toMatchObject({ ticket: 'x'.repeat(512) });
  });

  it('rejects with an AppError carrying Retry-After on 429', async () => {
    const { repository, http } = setup();
    const result = repository.issue();
    http.expectOne(URL).flush({ code: 'rate_limited' }, { status: 429, statusText: 'Too Many', headers: { 'Retry-After': '12' } });
    const error = (await result.then(
      () => null,
      (cause: unknown) => cause,
    )) as AppError;
    expect(error.status).toBe(429);
    expect(error.kind).toBe('rate_limit');
    expect(error.retryAfterSec).toBe(12);
  });
});
