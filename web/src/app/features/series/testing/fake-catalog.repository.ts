import type { AppError, Result } from '@core/errors';
import { CatalogRepository, type VoiceCatalogResponse } from '../application/catalog.repository';
import type { Language, Voice } from '../domain/voice.model';

/** Canned catalog: a vi voice without preview, a vi voice with preview, an en voice, a central-tagged vi voice. */
export const CANNED_VOICES: readonly Voice[] = [
  { id: 'vi-quiet', displayName: 'Minh', language: 'vi', gender: 'male', tags: ['calm', 'northern'], previewUrl: null },
  { id: 'vi-warm', displayName: 'Lan', language: 'vi', gender: 'female', tags: ['warm', 'storyteller'], previewUrl: 'https://cdn.example.com/p/vi-warm.mp3' },
  { id: 'en-news', displayName: 'Ava', language: 'en', gender: 'female', tags: ['news'], previewUrl: 'https://cdn.example.com/p/en-news.mp3' },
  { id: 'vi-hue', displayName: 'Thu', language: 'vi', gender: 'female', tags: ['central', 'whisper'], previewUrl: null },
];

interface Pending {
  readonly language: Language;
  readonly resolve: (result: Result<VoiceCatalogResponse>) => void;
}

/**
 * Hand-written scriptable `CatalogRepository` (README D15: no mocking library). Answers the canned catalog filtered by
 * language; `failWith` scripts a failure, `answerNotModified` a 304. In deferred mode a call stays pending until
 * `resolveNext`/`resolveLanguage`, which is how a spec reorders responses.
 */
export class FakeCatalogRepository extends CatalogRepository {
  /** One entry per `voices()` call, in call order. */
  readonly voicedCalls: Language[] = [];
  /** The `ifNoneMatch` argument of each call (parallel to `voicedCalls`). */
  readonly ifNoneMatchCalls: Array<string | null> = [];
  private catalog: readonly Voice[] = CANNED_VOICES;
  private etag: string | null = null;
  private failure: AppError | null = null;
  private notModified = false;
  private deferred = false;
  private readonly pending: Pending[] = [];

  /** Replaces the catalog served by later calls. */
  willServe(voices: readonly Voice[], etag: string | null = null): this {
    this.catalog = voices;
    this.etag = etag;
    return this;
  }

  failWith(error: AppError | null): this {
    this.failure = error;
    return this;
  }

  answerNotModified(on = true): this {
    this.notModified = on;
    return this;
  }

  /** Subsequent calls stay pending until resolved. */
  defer(on = true): this {
    this.deferred = on;
    return this;
  }

  get pendingCount(): number {
    return this.pending.length;
  }

  /** Resolves the oldest pending call with the scripted answer. */
  resolveNext(): void {
    const next = this.pending.shift();
    if (next === undefined) throw new Error('no pending call');
    next.resolve(this.answer(next.language));
  }

  /** Resolves the oldest pending call for `language`, whatever its position in the queue. */
  resolveLanguage(language: Language): void {
    const index = this.pending.findIndex((entry) => entry.language === language);
    if (index < 0) throw new Error(`no pending call for ${language}`);
    const [entry] = this.pending.splice(index, 1);
    entry!.resolve(this.answer(language));
  }

  override voices(language: Language, ifNoneMatch: string | null = null): Promise<Result<VoiceCatalogResponse>> {
    this.voicedCalls.push(language);
    this.ifNoneMatchCalls.push(ifNoneMatch);
    if (this.deferred) {
      return new Promise((resolve) => {
        this.pending.push({ language, resolve });
      });
    }
    return Promise.resolve(this.answer(language));
  }

  private answer(language: Language): Result<VoiceCatalogResponse> {
    if (this.failure !== null) return { ok: false, error: this.failure };
    if (this.notModified) return { ok: true, value: { kind: 'notModified' } };
    return { ok: true, value: { kind: 'ok', voices: this.catalog.filter((voice) => voice.language === language), etag: this.etag } };
  }
}
