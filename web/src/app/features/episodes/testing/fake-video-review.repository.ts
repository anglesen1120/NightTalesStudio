import {
  VideoReviewRepository,
  type RejectTarget,
  type VideoApproveAnswer,
  type VideoDecisionAnswer,
  type VideoRejectIntent,
  type VideoUrl,
} from '../domain/video-review.repository';

interface Pending<T> {
  readonly resolve: (value: T) => void;
  readonly reject: (error: unknown) => void;
}

/**
 * One method of the fake: the scripted answers of the next calls, the calls held open by `defer()`, and the default
 * answer used when no script is left. The three port methods need exactly this lockstep behaviour, so it lives once.
 */
class ScriptedMethod<T> {
  private readonly script: Array<() => Promise<T>> = [];
  private readonly pending: Array<Pending<T>> = [];
  private deferred = false;

  constructor(private readonly fallback: () => T) {}

  /** The next call answers `answer`. */
  willAnswer(answer: () => Promise<T>): void {
    this.script.push(answer);
  }

  /** The next call fails with any scripted cause (an `AppError`, a network failure, a timeout). */
  willFail(error: unknown): void {
    // eslint-disable-next-line @typescript-eslint/prefer-promise-reject-errors -- scripts the port's structured AppError rejection
    this.script.push(() => Promise.reject(error));
  }

  /** Leaves every later call pending until `resolve`/`reject`. */
  defer(on = true): void {
    this.deferred = on;
  }

  next(): Promise<T> {
    const answer = this.script.shift();
    if (answer !== undefined) return answer();
    if (!this.deferred) return Promise.resolve(this.fallback());
    return new Promise<T>((resolve, reject) => {
      this.pending.push({ resolve, reject });
    });
  }

  get pendingCount(): number {
    return this.pending.length;
  }

  /** Resolves the oldest (or the given index of the) pending call. */
  resolve(value: T, index = 0): void {
    this.take(index).resolve(value);
  }

  reject(error: unknown, index = 0): void {
    this.take(index).reject(error);
  }

  private take(index: number): Pending<T> {
    const entry = this.pending.splice(index, 1)[0];
    if (entry === undefined) throw new RangeError(`no pending call at index ${index}`);
    return entry;
  }
}

/** One recorded call of the fake, so a spec can prove which arguments (and which intent OBJECT) reached the port. */
export type VideoReviewCall =
  | { readonly method: 'getVideoUrl'; readonly episodeId: string; readonly userAction: boolean }
  | { readonly method: 'approveVideo'; readonly episodeId: string; readonly etag: string; readonly note: string | undefined }
  | {
      readonly method: 'rejectVideo';
      readonly episodeId: string;
      readonly etag: string;
      readonly reason: string;
      readonly target: RejectTarget;
      readonly intent: VideoRejectIntent;
    };

/**
 * Hand-written scriptable `VideoReviewRepository` (README D15: no mocking library). Every call records its arguments —
 * including the reject's intent OBJECT (an `IdempotencyIntent`, never a bare key string), so a spec can prove which
 * intent a retry replayed and that the repository receives the object `withIdempotency` needs — and answers the next
 * scripted reply. `defer*()` keeps calls pending so a spec can resolve or reject them out of order (a lost response,
 * a second click, a refresh that races the initial read).
 */
export class FakeVideoReviewRepository extends VideoReviewRepository {
  readonly calls: VideoReviewCall[] = [];
  /** The URL an unscripted `getVideoUrl` answers; a spec replaces it to model a refresh that hands out a new link. */
  url = 'https://media.example.test/ep-1.mp4?sig=1';
  expiresAt = '2026-10-19T10:15:00.000+00:00';

  private readonly urlMethod = new ScriptedMethod<VideoUrl>(() => ({ url: this.url, expiresAt: this.expiresAt }));
  private readonly approveMethod = new ScriptedMethod<VideoApproveAnswer>(() => ({ status: 'ready', etag: '"ep-2"' }));
  private readonly rejectMethod = new ScriptedMethod<VideoDecisionAnswer>(() => ({ status: 'story_review', etag: '"ep-2"', accepted: false }));

  get urlCalls(): ReadonlyArray<Extract<VideoReviewCall, { method: 'getVideoUrl' }>> {
    return this.calls.filter((call): call is Extract<VideoReviewCall, { method: 'getVideoUrl' }> => call.method === 'getVideoUrl');
  }

  get approveCalls(): ReadonlyArray<Extract<VideoReviewCall, { method: 'approveVideo' }>> {
    return this.calls.filter((call): call is Extract<VideoReviewCall, { method: 'approveVideo' }> => call.method === 'approveVideo');
  }

  get rejectCalls(): ReadonlyArray<Extract<VideoReviewCall, { method: 'rejectVideo' }>> {
    return this.calls.filter((call): call is Extract<VideoReviewCall, { method: 'rejectVideo' }> => call.method === 'rejectVideo');
  }

  get pendingCount(): number {
    return this.urlMethod.pendingCount + this.approveMethod.pendingCount + this.rejectMethod.pendingCount;
  }

  willAnswerUrl(url: VideoUrl): this {
    this.urlMethod.willAnswer(() => Promise.resolve(url));
    return this;
  }

  willFailUrl(error: unknown): this {
    this.urlMethod.willFail(error);
    return this;
  }

  willAcceptApprove(answer: VideoApproveAnswer = { status: 'ready', etag: '"ep-2"' }): this {
    this.approveMethod.willAnswer(() => Promise.resolve(answer));
    return this;
  }

  willFailApprove(error: unknown): this {
    this.approveMethod.willFail(error);
    return this;
  }

  /** The reject answer; `accepted: true` models the `re_render` 202 (no `Location`, no job id). */
  willAcceptReject(answer: VideoDecisionAnswer = { status: 'story_review', etag: '"ep-2"', accepted: false }): this {
    this.rejectMethod.willAnswer(() => Promise.resolve(answer));
    return this;
  }

  willFailReject(error: unknown): this {
    this.rejectMethod.willFail(error);
    return this;
  }

  deferUrl(on = true): this {
    this.urlMethod.defer(on);
    return this;
  }

  deferApprove(on = true): this {
    this.approveMethod.defer(on);
    return this;
  }

  deferReject(on = true): this {
    this.rejectMethod.defer(on);
    return this;
  }

  resolveUrl(value: VideoUrl, index = 0): void {
    this.urlMethod.resolve(value, index);
  }

  rejectUrl(error: unknown, index = 0): void {
    this.urlMethod.reject(error, index);
  }

  resolveApprove(value: VideoApproveAnswer, index = 0): void {
    this.approveMethod.resolve(value, index);
  }

  rejectApprove(error: unknown, index = 0): void {
    this.approveMethod.reject(error, index);
  }

  resolveReject(value: VideoDecisionAnswer, index = 0): void {
    this.rejectMethod.resolve(value, index);
  }

  rejectReject(error: unknown, index = 0): void {
    this.rejectMethod.reject(error, index);
  }

  override getVideoUrl(episodeId: string, opts?: { readonly userAction?: boolean }): Promise<VideoUrl> {
    this.calls.push({ method: 'getVideoUrl', episodeId, userAction: opts?.userAction === true });
    return this.urlMethod.next();
  }

  override approveVideo(episodeId: string, etag: string, note?: string): Promise<VideoApproveAnswer> {
    this.calls.push({ method: 'approveVideo', episodeId, etag, note });
    return this.approveMethod.next();
  }

  override rejectVideo(
    episodeId: string,
    etag: string,
    reason: string,
    target: RejectTarget,
    intent: VideoRejectIntent,
  ): Promise<VideoDecisionAnswer> {
    this.calls.push({ method: 'rejectVideo', episodeId, etag, reason, target, intent });
    return this.rejectMethod.next();
  }
}
