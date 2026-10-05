import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { afterEach, describe, expect, it, vi } from 'vitest';
import type { AppError } from '@core/errors';
import type { MeDto } from '@shared/api';
import { ZONELESS_TEST_PROVIDERS } from '../../../testing/providers';
import { MeHttpRepository, toMeSummary } from './me.http-repository';

// QA (10.2.6-T1): hostile / skewed MeDto shapes and failure statuses for the /me mapping and the repository.

function dto(overrides: Record<string, unknown> = {}): MeDto {
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
  } as unknown as MeDto;
}

describe('toMeSummary (QA)', () => {
  afterEach(() => vi.restoreAllMocks());

  it('features undefined, null and omitted all map to []', () => {
    expect(toMeSummary(dto({ features: undefined })).features).toEqual([]);
    expect(toMeSummary(dto({ features: null })).features).toEqual([]);
    const rest = { ...(dto() as unknown as Record<string, unknown>) };
    delete rest['features'];
    expect(toMeSummary(rest as unknown as MeDto).features).toEqual([]);
  });

  it('the spec example keeps only the two known keys and drops admin_panel', () => {
    const summary = toMeSummary(dto({ features: ['source_finder', 'youtube_publish', 'admin_panel'] }));
    expect(summary.features).toEqual(['source_finder', 'youtube_publish']);
    expect((summary.features as readonly string[]).includes('tiktok_publish')).toBe(false);
  });

  it('non-string / prototype-ish / differently-cased keys are dropped', () => {
    const summary = toMeSummary(
      dto({ features: [1, null, {}, ['source_finder'], '__proto__', 'constructor', 'SOURCE_FINDER', ' source_finder', 'source_finder'] }),
    );
    expect(summary.features).toEqual(['source_finder']);
  });

  it('duplicate known keys do not crash and keep hasFeature semantics (present)', () => {
    const summary = toMeSummary(dto({ features: ['source_finder', 'source_finder'] }));
    expect(summary.features.includes('source_finder')).toBe(true);
    expect(new Set(summary.features).size).toBe(1);
    // Expected: de-duplicated. Actual (if this fails): duplicates leak into the snapshot.
    expect(summary.features).toEqual(['source_finder']);
  });

  it('a non-array features member (string / object / number) must degrade to [] instead of throwing', () => {
    for (const bad of ['source_finder', { 0: 'source_finder', length: 1 }, 7, true]) {
      expect(() => toMeSummary(dto({ features: bad }))).not.toThrow();
      expect(toMeSummary(dto({ features: bad })).features).toEqual([]);
    }
  });

  it.each(['fr', '', null, undefined, 'EN', 'en-US', 7, {}])('uiLocale %j falls back to vi', (uiLocale) => {
    vi.spyOn(console, 'warn').mockImplementation(() => undefined);
    expect(toMeSummary(dto({ uiLocale })).uiLocale).toBe('vi');
  });

  it('does not alias the dto features array (replaced, not mutated)', () => {
    const features = ['source_finder'];
    const summary = toMeSummary(dto({ features }));
    features.push('youtube_publish');
    expect(summary.features).toEqual(['source_finder']);
  });
});

describe('MeHttpRepository (QA failures)', () => {
  afterEach(() => TestBed.inject(HttpTestingController).verify());

  function setup() {
    TestBed.configureTestingModule({
      providers: [...ZONELESS_TEST_PROVIDERS, provideHttpClient(), provideHttpClientTesting(), MeHttpRepository],
    });
    return { repo: TestBed.inject(MeHttpRepository), http: TestBed.inject(HttpTestingController) };
  }

  it.each([400, 401, 403, 404, 500, 503])('PATCH %i rejects with an AppError carrying that status', async (status) => {
    const { repo, http } = setup();
    const result = repo.updateUiLocale('en');
    http.expectOne('/api/v1/me').flush({ code: 'x' }, { status, statusText: 'err' });
    const error = (await result.then(
      () => null,
      (e: unknown) => e,
    )) as AppError | null;
    expect(error).not.toBeNull();
    expect(error?.status).toBe(status);
  });

  it('a network error (status 0) rejects with an AppError, not a raw HttpErrorResponse', async () => {
    const { repo, http } = setup();
    const result = repo.get();
    http.expectOne('/api/v1/me').error(new ProgressEvent('error'));
    const error = (await result.then(
      () => null,
      (e: unknown) => e,
    )) as AppError | null;
    expect(error).not.toBeNull();
    expect(typeof error?.code).toBe('string');
    expect(typeof error?.kind).toBe('string');
  });

  it('a 200 with an unusable body (null) rejects with an AppError rather than a raw TypeError', async () => {
    const { repo, http } = setup();
    const result = repo.get();
    http.expectOne('/api/v1/me').flush(null);
    const error = (await result.then(
      () => null,
      (e: unknown) => e,
    )) as AppError | null;
    expect(error).not.toBeNull();
    expect(typeof error?.status).toBe('number');
  });
});
