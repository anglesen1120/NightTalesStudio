import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { afterEach, describe, expect, it } from 'vitest';
import { USER_ACTION } from '@core/http/http-context-tokens';
import { ZONELESS_TEST_PROVIDERS } from '../../../testing/providers';
import { LegalAcceptanceRepository } from './legal-acceptance.repository';

/** QA probes for 13.2.1-T1: the wire contract the backend tests pin from the other side. */

function setup(): { repository: LegalAcceptanceRepository; http: HttpTestingController } {
  TestBed.configureTestingModule({
    providers: [...ZONELESS_TEST_PROVIDERS, provideHttpClient(), provideHttpClientTesting()],
  });
  return { repository: TestBed.inject(LegalAcceptanceRepository), http: TestBed.inject(HttpTestingController) };
}

afterEach(() => {
  TestBed.inject(HttpTestingController).verify();
  TestBed.resetTestingModule();
});

describe('LegalAcceptanceRepository', () => {
  it('GETs /api/v1/me/legal as a user action with no query string or body', async () => {
    const { repository, http } = setup();
    const result = repository.get();
    const request = http.expectOne('/api/v1/me/legal');
    expect(request.request.method).toBe('GET');
    expect(request.request.context.get(USER_ACTION)).toBe(true);
    expect(request.request.params.keys()).toEqual([]);
    request.flush({ documents: [] });
    await expect(result).resolves.toEqual({ documents: [] });
  });

  it('POSTs exactly { tos, privacy } to /api/v1/me/legal/accept — no user id, no extra members', async () => {
    const { repository, http } = setup();
    const result = repository.accept('1.0.0', '1.0.0');
    const request = http.expectOne('/api/v1/me/legal/accept');
    expect(request.request.method).toBe('POST');
    expect(request.request.context.get(USER_ACTION)).toBe(true);
    expect(request.request.body).toEqual({ tos: '1.0.0', privacy: '1.0.0' });
    expect(Object.keys(request.request.body as object).sort()).toEqual(['privacy', 'tos']);
    request.flush({ documents: [] });
    await result;
  });

  it('lets an HTTP error reach the caller unchanged', async () => {
    const { repository, http } = setup();
    const result = repository.accept('1.0.0', '1.0.0');
    http.expectOne('/api/v1/me/legal/accept').flush({ code: 'legal_version_outdated' }, { status: 409, statusText: 'Conflict' });
    await expect(result).rejects.toMatchObject({ status: 409 });
  });
});
