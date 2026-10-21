import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { afterEach, describe, expect, it, vi } from 'vitest';
import type { AppError } from '@core/errors';
import { ZONELESS_TEST_PROVIDERS } from '../../../testing/providers';
import { MeHttpRepository } from './me.http-repository';

const URL = '/api/v1/me';

function dto(overrides: Record<string, unknown> = {}): Record<string, unknown> {
  return {
    id: 'u-1',
    displayName: 'An',
    email: 'an@example.test',
    uiLocale: 'vi',
    role: 'user',
    mfaEnabled: false,
    lastLoginAt: null,
    features: [],
    ...overrides,
  };
}

function setup(): { repository: MeHttpRepository; http: HttpTestingController } {
  TestBed.configureTestingModule({
    providers: [...ZONELESS_TEST_PROVIDERS, provideHttpClient(), provideHttpClientTesting(), MeHttpRepository],
  });
  return { repository: TestBed.inject(MeHttpRepository), http: TestBed.inject(HttpTestingController) };
}

describe('MeHttpRepository', () => {
  afterEach(() => {
    TestBed.inject(HttpTestingController).verify();
    vi.restoreAllMocks();
  });

  it('GETs /api/v1/me and maps the DTO into a MeSummary', async () => {
    const { repository, http } = setup();
    const result = repository.get();
    const req = http.expectOne(URL);
    expect(req.request.method).toBe('GET');
    req.flush(dto({ features: ['source_finder'], uiLocale: 'en', role: 'owner' }));
    await expect(result).resolves.toEqual({
      id: 'u-1',
      displayName: 'An',
      email: 'an@example.test',
      uiLocale: 'en',
      role: 'owner',
      features: ['source_finder'],
    });
  });

  it('PATCHes exactly { uiLocale: "en" } with no extra member and no idempotency key', async () => {
    const { repository, http } = setup();
    const result = repository.updateUiLocale('en');
    const req = http.expectOne(URL);
    expect(req.request.method).toBe('PATCH');
    expect(req.request.body).toEqual({ uiLocale: 'en' });
    expect(Object.keys(req.request.body as object)).toEqual(['uiLocale']);
    expect(req.request.headers.has('Idempotency-Key')).toBe(false);
    req.flush(dto({ uiLocale: 'en' }));
    await expect(result).resolves.toMatchObject({ uiLocale: 'en' });
  });

  it('keeps only allow-listed features (unknown keys dropped)', async () => {
    const { repository, http } = setup();
    const result = repository.get();
    http.expectOne(URL).flush(dto({ features: ['source_finder', 'youtube_publish', 'admin_panel'] }));
    await expect(result).resolves.toMatchObject({ features: ['source_finder', 'youtube_publish'] });
  });

  it('maps a missing features member to an empty array', async () => {
    const { repository, http } = setup();
    const result = repository.get();
    const body = dto();
    delete body['features'];
    http.expectOne(URL).flush(body);
    await expect(result).resolves.toMatchObject({ features: [] });
  });

  it('falls back to vi for an unknown server locale and warns once in dev mode', async () => {
    const warn = vi.spyOn(console, 'warn').mockImplementation(() => undefined);
    const { repository, http } = setup();
    const result = repository.get();
    http.expectOne(URL).flush(dto({ uiLocale: 'fr' }));
    await expect(result).resolves.toMatchObject({ uiLocale: 'vi' });
    expect(warn).toHaveBeenCalledTimes(1);
  });

  it('rejects with an AppError on a server failure', async () => {
    const { repository, http } = setup();
    const result = repository.get();
    http.expectOne(URL).flush({ code: 'server_error' }, { status: 503, statusText: 'Unavailable' });
    const error = (await result.then(
      () => null,
      (cause: unknown) => cause,
    )) as AppError;
    expect(error.status).toBe(503);
    expect(error.kind).toBe('server');
  });
});
