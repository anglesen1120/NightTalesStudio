import type { AppError } from '@core/errors';
import type { AppLocale } from '@core/i18n';
import type { MeSummary } from '../me-summary';
import { MeRepository } from '../me.repository';

interface Pending {
  readonly resolve: (summary: MeSummary) => void;
  readonly reject: (error: AppError) => void;
}

export function fakeMeSummary(overrides: Partial<MeSummary> = {}): MeSummary {
  return { id: 'user-1', uiLocale: 'vi', role: 'user', features: [], displayName: 'An', email: 'an@example.test', ...overrides };
}

/**
 * Hand-written scriptable `MeRepository` (README D15). `getCalls`/`patchCalls` count requests, `patchedLocales` records
 * every `updateUiLocale` argument. `failure` rejects the next call of either kind once; `deferred` makes both wait for
 * `resolveNext`/`rejectNext` (FIFO, one queue per kind).
 */
export class FakeMeRepository extends MeRepository {
  getCalls = 0;
  patchCalls = 0;
  readonly patchedLocales: AppLocale[] = [];
  /** Returned by `get()`; `updateUiLocale` returns it with the requested locale applied. */
  profile: MeSummary = fakeMeSummary();
  failure: AppError | null = null;
  deferred = false;
  private readonly pendingGets: Pending[] = [];
  private readonly pendingPatches: Pending[] = [];

  static serverError(): AppError {
    return { code: 'server_error', status: 503, kind: 'server', fieldErrors: {} };
  }

  static unauthorized(): AppError {
    return { code: 'unauthorized', status: 401, kind: 'auth', fieldErrors: {} };
  }

  override get(): Promise<MeSummary> {
    this.getCalls += 1;
    return this.answer(this.pendingGets, this.profile);
  }

  override updateUiLocale(locale: AppLocale): Promise<MeSummary> {
    this.patchCalls += 1;
    this.patchedLocales.push(locale);
    return this.answer(this.pendingPatches, { ...this.profile, uiLocale: locale });
  }

  resolveNextGet(summary: MeSummary = this.profile): void {
    this.pendingGets.shift()?.resolve(summary);
  }

  rejectNextGet(error: AppError): void {
    this.pendingGets.shift()?.reject(error);
  }

  resolveNextPatch(summary?: MeSummary): void {
    this.pendingPatches.shift()?.resolve(summary ?? this.profile);
  }

  rejectNextPatch(error: AppError): void {
    this.pendingPatches.shift()?.reject(error);
  }

  private answer(queue: Pending[], summary: MeSummary): Promise<MeSummary> {
    const failure = this.failure;
    if (failure !== null && !this.deferred) {
      this.failure = null;
      // eslint-disable-next-line @typescript-eslint/prefer-promise-reject-errors -- the port rejects with an AppError by contract
      return Promise.reject(failure);
    }
    if (!this.deferred) return Promise.resolve(summary);
    return new Promise<MeSummary>((resolve, reject) => {
      queue.push({ resolve, reject });
    });
  }
}
