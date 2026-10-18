import { RenderRepository, type RenderCommandIntent } from '../domain/render.repository';

/** One pending deferred call. */
interface Pending<T> {
  readonly resolve: (value: T) => void;
  readonly reject: (error: unknown) => void;
}

/** One scripted answer per call; a call with no script left answers the seeded/default value. */
type Answer<T> = () => Promise<T>;

/** A promise a spec resolves later through the fake's `resolve*`/`reject*` helpers. */
function pendingInto<T>(queue: Array<Pending<T>>): Promise<T> {
  return new Promise<T>((resolve, reject) => {
    queue.push({ resolve, reject });
  });
}

/**
 * Hand-written scriptable `RenderRepository` (README D15: no mocking library). Every call records its arguments —
 * including the intent OBJECT itself (an `IdempotencyIntent`, never a bare key string), so a spec can prove which
 * intent a retry replayed and that the repository receives the object `withIdempotency` needs — and answers the next
 * scripted reply. `deferRender()`/`deferCancel()` keep every call pending so a spec can resolve or reject out of
 * order (a lost response, a second click, a cancel that races the terminal event).
 */
export class FakeRenderRepository extends RenderRepository {
  readonly requestCalls: Array<{ episodeId: string; etag: string; intent: RenderCommandIntent }> = [];
  /** The render job ids `cancelRender` was called with, in call order. */
  readonly cancelCalls: string[] = [];
  /** The id a successful `requestRender` answers with (the 202's `Location` id). */
  renderJobId = 'job-1';

  private readonly requestScript: Array<Answer<string>> = [];
  private readonly cancelScript: Array<Answer<void>> = [];
  private readonly requestPending: Array<Pending<string>> = [];
  private readonly cancelPending: Array<Pending<void>> = [];
  private deferredRequest = false;
  private deferredCancelCall = false;

  /** The next `requestRender` answers the given job id (default: the seeded one). */
  willAcceptRender(jobId = this.renderJobId): this {
    this.requestScript.push(() => Promise.resolve(jobId));
    return this;
  }

  /** The next `requestRender` fails with any scripted cause (an `AppError`, a network failure, a timeout). */
  willFailRender(error: unknown): this {
    // eslint-disable-next-line @typescript-eslint/prefer-promise-reject-errors -- scripts the port's structured AppError rejection
    this.requestScript.push(() => Promise.reject(error));
    return this;
  }

  willAcceptCancel(): this {
    this.cancelScript.push(() => Promise.resolve());
    return this;
  }

  willFailCancel(error: unknown): this {
    // eslint-disable-next-line @typescript-eslint/prefer-promise-reject-errors -- scripts the port's structured AppError rejection
    this.cancelScript.push(() => Promise.reject(error));
    return this;
  }

  /** Leaves every later `requestRender` pending until `resolveRender`/`rejectRender`. */
  deferRender(on = true): this {
    this.deferredRequest = on;
    return this;
  }

  deferCancel(on = true): this {
    this.deferredCancelCall = on;
    return this;
  }

  /** How many `requestRender` calls are still waiting for `resolveRender`/`rejectRender`. */
  get requestPendingCount(): number {
    return this.requestPending.length;
  }

  get pendingCount(): number {
    return this.requestPending.length + this.cancelPending.length;
  }

  /** Resolves the oldest (or the given index of the) pending `requestRender`. */
  resolveRender(jobId = this.renderJobId, index = 0): void {
    this.take(this.requestPending, index).resolve(jobId);
  }

  rejectRender(error: unknown, index = 0): void {
    this.take(this.requestPending, index).reject(error);
  }

  /** Resolves the oldest (or the given index of the) pending `cancelRender`. */
  resolveCancel(index = 0): void {
    this.take(this.cancelPending, index).resolve(undefined);
  }

  rejectCancel(error: unknown, index = 0): void {
    this.take(this.cancelPending, index).reject(error);
  }

  override requestRender(episodeId: string, etag: string, intent: RenderCommandIntent): Promise<string> {
    this.requestCalls.push({ episodeId, etag, intent });
    if (this.deferredRequest) return pendingInto(this.requestPending);
    return this.requestScript.shift()?.() ?? Promise.resolve(this.renderJobId);
  }

  override cancelRender(renderJobId: string): Promise<void> {
    this.cancelCalls.push(renderJobId);
    if (this.deferredCancelCall) return pendingInto(this.cancelPending);
    return this.cancelScript.shift()?.() ?? Promise.resolve();
  }

  private take<T>(queue: Array<Pending<T>>, index: number): Pending<T> {
    const [taken] = queue.splice(index, 1);
    if (taken === undefined) throw new Error('no pending call');
    return taken;
  }
}
