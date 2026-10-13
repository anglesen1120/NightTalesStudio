import type { StoryVersionDetail, StoryVersionSummary } from '../domain/story-version.model';
import { StoryVersionRepository } from '../domain/story-version.repository';
import { storyContent } from './fake-story.repository';

export interface VersionListCall { readonly episodeId: string; readonly userAction: boolean }
export interface VersionDetailCall { readonly episodeId: string; readonly versionId: string; readonly userAction: boolean }

/**
 * Hand-written scriptable `StoryVersionRepository` (README D15: no mocking library). Each call answers the next
 * scripted response; `willFail*` scripts one failure, and a call with no script falls back to the seeded result.
 */
export class FakeStoryVersionRepository extends StoryVersionRepository {
  readonly listCalls: VersionListCall[] = [];
  readonly detailCalls: VersionDetailCall[] = [];
  listResult: readonly StoryVersionSummary[] = [];
  readonly details = new Map<string, StoryVersionDetail>();
  readonly detailFailures = new Map<string, unknown>();
  private listFailure: unknown = null;
  private readonly listScript: Array<() => Promise<readonly StoryVersionSummary[]>> = [];
  private readonly detailScript: Array<() => Promise<StoryVersionDetail>> = [];

  willFailList(error: unknown): this { this.listFailure = error; return this; }
  willFailDetail(versionId: string, error: unknown): this { this.detailFailures.set(versionId, error); return this; }
  scriptList(answer: () => Promise<readonly StoryVersionSummary[]>): this { this.listScript.push(answer); return this; }
  scriptDetail(answer: () => Promise<StoryVersionDetail>): this { this.detailScript.push(answer); return this; }

  seed(list: readonly StoryVersionSummary[], details: readonly StoryVersionDetail[] = []): this {
    this.listResult = list;
    this.details.clear();
    for (const detail of details) this.details.set(detail.versionId, detail);
    return this;
  }

  override list(episodeId: string, opts?: { userAction?: boolean }): Promise<readonly StoryVersionSummary[]> {
    this.listCalls.push({ episodeId, userAction: opts?.userAction === true });
    const scripted = this.listScript.shift();
    if (scripted !== undefined) return scripted();
    if (this.listFailure !== null) {
      // eslint-disable-next-line @typescript-eslint/prefer-promise-reject-errors -- scripts any wire failure, incl. AppError objects
      return Promise.reject(this.listFailure);
    }
    return Promise.resolve(this.listResult);
  }

  override detail(episodeId: string, versionId: string, opts?: { userAction?: boolean }): Promise<StoryVersionDetail> {
    this.detailCalls.push({ episodeId, versionId, userAction: opts?.userAction === true });
    const scripted = this.detailScript.shift();
    if (scripted !== undefined) return scripted();
    const failure = this.detailFailures.get(versionId);
    if (failure !== undefined) {
      // eslint-disable-next-line @typescript-eslint/prefer-promise-reject-errors -- scripts any wire failure, incl. AppError objects
      return Promise.reject(failure);
    }
    return Promise.resolve(this.details.get(versionId) ?? versionDetail(1, { versionId }));
  }
}

/** A summary row; `n` makes the id and version unique and ordered (server order is version DESC). */
export function versionSummary(n: number, overrides: Partial<StoryVersionSummary> = {}): StoryVersionSummary {
  return {
    versionId: `v${String(n)}`,
    version: n,
    source: 'generated',
    createdAt: '2026-10-01T10:00:00Z',
    wordCount: 100,
    etag: `"sv-v${String(n)}"`,
    similarityScore: null,
    approved: false,
    isCurrent: false,
    ...overrides,
  };
}

export function versionDetail(n: number, overrides: Partial<StoryVersionDetail> = {}): StoryVersionDetail {
  return {
    ...versionSummary(n),
    body: `Version ${String(n)} body.`,
    content: storyContent(),
    createdBy: 'owner@example.com',
    ...overrides,
  };
}
