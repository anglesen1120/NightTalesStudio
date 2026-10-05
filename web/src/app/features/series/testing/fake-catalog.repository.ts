import type { AppError, Result } from '@core/errors';
import { CatalogRepository, type StyleTemplateCatalogResponse, type VoiceCatalogResponse } from '../application/catalog.repository';
import type { Topic } from '../domain/series.model';
import type { StyleTemplate } from '../domain/style-template.model';
import { isTopicCompatible } from '../domain/topic-style-compatibility';
import type { Language, Voice } from '../domain/voice.model';

/** Canned catalog: a vi voice without preview, a vi voice with preview, an en voice, a central-tagged vi voice. */
export const CANNED_VOICES: readonly Voice[] = [
  { id: 'vi-quiet', displayName: 'Minh', language: 'vi', gender: 'male', tags: ['calm', 'northern'], previewUrl: null },
  { id: 'vi-warm', displayName: 'Lan', language: 'vi', gender: 'female', tags: ['warm', 'storyteller'], previewUrl: 'https://cdn.example.com/p/vi-warm.mp3' },
  { id: 'en-news', displayName: 'Ava', language: 'en', gender: 'female', tags: ['news'], previewUrl: 'https://cdn.example.com/p/en-news.mp3' },
  { id: 'vi-hue', displayName: 'Thu', language: 'vi', gender: 'female', tags: ['central', 'whisper'], previewUrl: null },
];

/** The seven seed templates of 03.1.1-T1 (three `ghost`, two `spiritual`, two `true_crime`). */
export const CANNED_STYLE_TEMPLATES: readonly StyleTemplate[] = [
  { id: 't-dark-folklore-ink', name: 'dark-folklore-ink', topicHint: 'ghost', previewUrl: 'https://cdn.example.com/p/dark-folklore-ink.png' },
  { id: 't-foggy-cinematic', name: 'foggy-cinematic', topicHint: 'ghost', previewUrl: 'https://cdn.example.com/p/foggy-cinematic.png' },
  { id: 't-vintage-photo-grain', name: 'vintage-photo-grain', topicHint: 'ghost', previewUrl: null },
  { id: 't-watercolor-night', name: 'watercolor-night', topicHint: 'spiritual', previewUrl: 'https://cdn.example.com/p/watercolor-night.png' },
  { id: 't-temple-candlelight', name: 'temple-candlelight', topicHint: 'spiritual', previewUrl: null },
  { id: 't-noir-sketch', name: 'noir-sketch', topicHint: 'true_crime', previewUrl: 'https://cdn.example.com/p/noir-sketch.png' },
  { id: 't-case-file-illustration', name: 'case-file-illustration', topicHint: 'true_crime', previewUrl: null },
];

interface Pending {
  readonly language: Language;
  readonly resolve: (result: Result<VoiceCatalogResponse>) => void;
}

interface PendingTemplates {
  readonly topic: Topic;
  readonly resolve: (result: Result<StyleTemplateCatalogResponse>) => void;
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
  /** One entry per `styleTemplates()` call, in call order. */
  readonly styleTemplateCalls: Topic[] = [];
  /** The `ifNoneMatch` argument of each `styleTemplates()` call (parallel to `styleTemplateCalls`). */
  readonly styleTemplateIfNoneMatchCalls: Array<string | null> = [];
  private catalog: readonly Voice[] = CANNED_VOICES;
  private etag: string | null = null;
  private templateCatalog: readonly StyleTemplate[] = CANNED_STYLE_TEMPLATES;
  private templateEtag: string | null = null;
  private failure: AppError | null = null;
  private notModified = false;
  private templatesNotModified = false;
  private deferred = false;
  private templatesDeferred = false;
  private readonly pending: Pending[] = [];
  private readonly pendingTemplates: PendingTemplates[] = [];

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

  override styleTemplates(topic: Topic, ifNoneMatch: string | null = null): Promise<Result<StyleTemplateCatalogResponse>> {
    this.styleTemplateCalls.push(topic);
    this.styleTemplateIfNoneMatchCalls.push(ifNoneMatch);
    if (this.templatesDeferred) {
      return new Promise((resolve) => {
        this.pendingTemplates.push({ topic, resolve });
      });
    }
    return Promise.resolve(this.answerTemplates(topic));
  }

  /** Replaces the templates served by later calls. */
  willServeTemplates(templates: readonly StyleTemplate[], etag: string | null = null): this {
    this.templateCatalog = templates;
    this.templateEtag = etag;
    return this;
  }

  answerTemplatesNotModified(on = true): this {
    this.templatesNotModified = on;
    return this;
  }

  /** Subsequent `styleTemplates()` calls stay pending until resolved. */
  deferTemplates(on = true): this {
    this.templatesDeferred = on;
    return this;
  }

  get pendingTemplateCount(): number {
    return this.pendingTemplates.length;
  }

  /** Resolves the oldest pending templates call with the scripted answer. */
  resolveNextTemplate(): void {
    const next = this.pendingTemplates.shift();
    if (next === undefined) throw new Error('no pending templates call');
    next.resolve(this.answerTemplates(next.topic));
  }

  /** Resolves the oldest pending templates call for `topic`, whatever its position in the queue. */
  resolveTemplate(topic: Topic): void {
    const index = this.pendingTemplates.findIndex((entry) => entry.topic === topic);
    if (index < 0) throw new Error(`no pending templates call for ${topic}`);
    const [entry] = this.pendingTemplates.splice(index, 1);
    entry!.resolve(this.answerTemplates(topic));
  }

  private answer(language: Language): Result<VoiceCatalogResponse> {
    if (this.failure !== null) return { ok: false, error: this.failure };
    if (this.notModified) return { ok: true, value: { kind: 'notModified' } };
    return { ok: true, value: { kind: 'ok', voices: this.catalog.filter((voice) => voice.language === language), etag: this.etag } };
  }

  /** The server's own rule: only templates the topic may use are served (03 README D2). */
  private answerTemplates(topic: Topic): Result<StyleTemplateCatalogResponse> {
    if (this.failure !== null) return { ok: false, error: this.failure };
    if (this.templatesNotModified) return { ok: true, value: { kind: 'notModified' } };
    const templates = this.templateCatalog.filter((template) => isTopicCompatible(topic, template));
    return { ok: true, value: { kind: 'ok', templates, etag: this.templateEtag } };
  }
}
