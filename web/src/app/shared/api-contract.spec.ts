// 10.1.4-T1: compile-then-run smoke over the generated client barrel (@shared/api).
//
// It lives outside shared/api/ on purpose: regenerating that directory must not delete a hand-written spec. Its job is
// to fail loudly when a regeneration renames the import path or the properties the rest of the app maps (C8/C14):
// the object literals below only compile while the generated types still carry those properties, so a rename breaks
// the build of this spec, not a feature three folders away. It is the only test over generated code; generated method
// bodies and operation names are deliberately not asserted.
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { inject } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { firstValueFrom, isObservable } from 'rxjs';
import { describe, expect, it } from 'vitest';
import {
  EpisodeEndpointsClient,
  provideApiConfiguration,
  type EpisodeDetailDto,
  type EpisodeListItemDto,
  type ProblemDetails,
  type RenderJobSummaryDto,
} from '@shared/api';
import { ZONELESS_TEST_PROVIDERS } from '../testing/providers';

const renderJob = {
  attempt: 1,
  errorCode: null,
  finishedAt: null,
  hints: [],
  imagesNeeded: 0,
  percent: 40,
  renderJobId: 'job-1',
  stage: 'images',
  status: 'running',
  submittedAt: null,
} satisfies RenderJobSummaryDto;

const detail = {
  brief: 'brief',
  currentStory: null,
  etag: '"v3"',
  finalVideo: null,
  generation: null,
  id: 'ep-1',
  lengthTier: 'short',
  nextActions: ['cancel'],
  publications: null,
  renderJob,
  seriesId: 'se-1',
  status: 'rendering',
  title: 'Title',
} satisfies EpisodeDetailDto;

describe('generated API client contract (@shared/api)', () => {
  it('exports the ProblemDetails fields the error mapper reads (10.2.2-T1)', () => {
    const problem = { code: 'validation_failed', traceId: 'trace-1', status: 422, title: 'Invalid', type: 'about:blank' } satisfies ProblemDetails;

    expect(Object.keys(problem)).toEqual(expect.arrayContaining(['status', 'code', 'traceId']));
  });

  it('exports the episode DTO fields the wave-1 folders map (C8, C14)', () => {
    const listItem = {
      createdAt: '2026-10-04T00:00:00Z',
      etag: '"v1"',
      id: 'ep-1',
      lengthTier: 'short',
      seriesId: 'se-1',
      status: 'draft',
      title: null,
      updatedAt: '2026-10-04T00:00:00Z',
    } satisfies EpisodeListItemDto;

    expect(Object.keys(detail)).toEqual(expect.arrayContaining(['etag', 'nextActions', 'renderJob', 'status']));
    expect(Object.keys(listItem)).toEqual(expect.arrayContaining(['etag', 'status']));
  });

  it('generates standalone Observable tag services; a data-access adapter turns them into a domain Promise', async () => {
    // The adapter shape D5 prescribes: the generated DTO never leaves data-access, the port returns Promise<model>.
    class EpisodeRepositoryAdapter {
      private readonly api = inject(EpisodeEndpointsClient);

      async load(id: string): Promise<{ id: string; status: string; etag: string }> {
        const dto = await firstValueFrom(this.api.apiV1EpisodesIdGet({ id }));
        return { id: dto.id, status: dto.status, etag: dto.etag };
      }
    }
    TestBed.configureTestingModule({
      providers: [...ZONELESS_TEST_PROVIDERS, provideHttpClient(), provideHttpClientTesting(), provideApiConfiguration(''), EpisodeRepositoryAdapter],
    });
    const api = TestBed.inject(EpisodeEndpointsClient);
    const http = TestBed.inject(HttpTestingController);

    expect(isObservable(api.apiV1EpisodesIdGet({ id: 'ep-1' }))).toBe(true);

    const model = TestBed.inject(EpisodeRepositoryAdapter).load('ep-1');
    const request = http.expectOne('/api/v1/episodes/ep-1');
    expect(request.request.method).toBe('GET');
    request.flush(detail);

    expect(model).toBeInstanceOf(Promise);
    expect(await model).toEqual({ id: 'ep-1', status: 'rendering', etag: '"v3"' });
    http.verify();
  });
});
