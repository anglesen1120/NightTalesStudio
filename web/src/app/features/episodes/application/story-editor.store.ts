import { DestroyRef, Injectable, computed, effect, inject, isDevMode, signal } from '@angular/core';
import type { WritableSignal } from '@angular/core';
import { AppErrorMapper } from '@core/errors';
import type { AppError } from '@core/errors';
import { RealtimeService } from '@core/realtime';
import type { RealtimeSubscription } from '@core/realtime';
import { SessionStore } from '@core/session';
import { addressForParagraph, formatAddress, parseAddress } from '@shared/util/segment-address';
import type { SegmentAddress } from '@shared/util/segment-address';
import { fromContent, serialize, toContent } from '../domain/story-codec';
import type { StoryDraft, StoryMetadata, StorySegment, StorySnapshot } from '../domain/story.model';
import { StoryRepository } from '../domain/story.repository';
import { countTokens } from '../domain/token-count';
import { EpisodeDetailStore, isAppError } from './episode-detail.store';

export interface ParagraphState {
  readonly segKey: string;
  readonly beat: string;
  readonly text: WritableSignal<string>;
  readonly keywords: WritableSignal<readonly string[]>;
  readonly factRefs: readonly unknown[];
}
const EMPTY_META: StoryMetadata = { title: '', description: '', tags: [], contentWarnings: [], structure: null };

/** Single page-scoped writer. ETags belong to the last adopted/saved story, not a dirty background read. */
@Injectable()
export class StoryEditorStore {
  private readonly repo = inject(StoryRepository);
  private readonly detail = inject(EpisodeDetailStore);
  private readonly mapper = inject(AppErrorMapper);
  private readonly realtime = inject(RealtimeService);
  private readonly session = inject(SessionStore);
  private readonly ownerGeneration = this.session.generation();
  readonly loading = signal(true);
  readonly error = signal<AppError | null>(null);
  readonly snapshot = signal<StorySnapshot | null>(null);
  readonly title = signal('');
  readonly hook = signal('');
  readonly ending = signal('');
  readonly meta = signal<StoryMetadata>(EMPTY_META);
  readonly paragraphs = signal<readonly ParagraphState[]>([]);
  readonly hookKeywords = signal<readonly string[]>([]);
  readonly endingKeywords = signal<readonly string[]>([]);
  readonly hookKey = signal('');
  readonly endingKey = signal('');
  readonly version = signal(0);
  readonly savedVersion = signal(0);
  private readonly baselineJson = signal('');
  private readonly dirtyJson = signal('');
  readonly isDirty = computed(() => this.snapshot() !== null && this.dirtyJson() !== this.baselineJson());
  readonly saving = signal(false);
  private readonly resolving = signal(false);
  readonly savedAt = signal<number | null>(null);
  readonly conflict = signal<{ serverEtag: string; serverVersion: number } | null>(null);
  readonly fieldErrors = signal<Readonly<Partial<Record<SegmentAddress, readonly string[]>>>>({});
  readonly formErrors = signal<readonly string[]>([]);
  readonly keywordLimitHit = signal<string | null>(null);
  readonly invalidState = signal(false);
  readonly readOnly = computed(() => !this.detail.nextActions().includes('edit_story'));
  readonly saveDisabled = computed(() => !this.active() || this.loading() || this.saving() || this.resolving() || this.readOnly() || this.invalidState() || !this.hasUnsavedChanges());
  readonly segmentTokens = computed<ReadonlyMap<SegmentAddress, number>>(() => {
    const tokens = new Map<SegmentAddress, number>([['title', countTokens(this.title())], ['hook', countTokens(this.hook())]]);
    this.paragraphs().forEach((paragraph, index) => tokens.set(addressForParagraph(index), countTokens(paragraph.text())));
    tokens.set('ending', countTokens(this.ending()));
    tokens.set('meta', countTokens(this.meta().description));
    return tokens;
  });
  readonly totalTokens = computed(() => [...this.segmentTokens().values()].reduce((sum, value) => sum + value, 0));
  private hookFacts: readonly unknown[] = [];
  private endingFacts: readonly unknown[] = [];
  private id: string | null = null;
  private generation = 0;
  private readGeneration = 0;
  private destroyed = false;
  private subscription: RealtimeSubscription | null = null;
  private dirtyTimer: number | undefined;
  private invalidationTimer: number | undefined;

  constructor() {
    inject(DestroyRef).onDestroy(() => {
      this.destroyed = true; this.generation++; this.readGeneration++;
      this.teardown(); this.clearDraft(); this.clearErrors();
    });
    effect(() => {
      if (this.session.generation() !== this.ownerGeneration) {
        this.generation++; this.readGeneration++; this.id = null;
        this.teardown(); this.clearDraft(); this.clearErrors(); this.loading.set(false); this.saving.set(false); this.resolving.set(false); this.invalidState.set(false);
      }
    });
  }

  async load(id: string): Promise<void> {
    if (!this.active()) return;
    this.generation++; this.readGeneration++;
    const generation = this.generation;
    this.teardown(); this.clearDraft(); this.id = id;
    this.loading.set(true); this.saving.set(false); this.invalidState.set(false); this.clearErrors();
    this.subscription = this.realtime.subscribeEpisode(id, () => {
      if (!this.current(generation)) return;
      window.clearTimeout(this.invalidationTimer);
      this.invalidationTimer = window.setTimeout(() => {
        this.invalidationTimer = undefined;
        if (this.current(generation)) void this.refresh(false, false);
      }, 250);
    });
    const result = await this.detail.load(id);
    if (!this.current(generation)) return;
    if (result !== null) this.adopt(result.currentStory);
    else this.error.set(this.detail.error());
    this.loading.set(false);
  }

  hasUnsavedChanges(): boolean {
    return this.snapshot() !== null && serialize(this.buildDraft()) !== this.baselineJson();
  }

  setText(segKey: string, text: string): void {
    if (!this.canEdit()) return;
    if (segKey === this.hookKey()) this.hook.set(text);
    else if (segKey === this.endingKey()) this.ending.set(text);
    else {
      const paragraph = this.paragraphs().find((item) => item.segKey === segKey);
      if (!paragraph) return;
      paragraph.text.set(text);
    }
    this.edited();
  }

  setSegmentText(kind: 'title' | 'hook' | 'ending', text: string): void {
    if (!this.canEdit()) return;
    this[kind].set(text);
    if (kind === 'title') this.meta.update((meta) => ({ ...meta, title: text }));
    this.edited();
  }

  addKeyword(segKey: string, keyword: string): void {
    const keywords = this.keywordSignal(segKey);
    if (!this.canEdit() || !keywords) return;
    if (keywords().length >= 2) { this.keywordLimitHit.set(segKey); return; }
    keywords.update((values) => [...values, keyword]); this.edited();
  }

  updateKeyword(segKey: string, index: number, keyword: string): void {
    const keywords = this.keywordSignal(segKey);
    if (!this.canEdit() || !keywords || !Number.isInteger(index) || index < 0 || index >= keywords().length) return;
    keywords.update((values) => values.map((value, i) => i === index ? keyword : value)); this.edited();
  }

  removeKeyword(segKey: string, index: number): void {
    const keywords = this.keywordSignal(segKey);
    if (!this.canEdit() || !keywords || !Number.isInteger(index) || index < 0 || index >= keywords().length) return;
    if (keywords().length <= 1) { this.keywordLimitHit.set(segKey); return; }
    keywords.update((values) => values.filter((_, i) => i !== index)); this.edited();
  }

  setMeta(patch: Partial<StoryMetadata>): void {
    if (!this.canEdit()) return;
    // structure is opaque/read-only and cannot be changed by presentation.
    const { title, description, tags, contentWarnings } = patch;
    this.meta.update((meta) => ({ ...meta,
      ...(title === undefined ? {} : { title }), ...(description === undefined ? {} : { description }),
      ...(tags === undefined ? {} : { tags: [...tags] }), ...(contentWarnings === undefined ? {} : { contentWarnings: [...contentWarnings] }),
    }));
    if (title !== undefined) this.title.set(title);
    this.edited();
  }

  async saveVersion(): Promise<void> {
    if (!this.canEdit() || this.saving() || this.resolving() || !this.hasUnsavedChanges()) return;
    await this.put(this.snapshot()?.etag ?? '');
  }

  async resolveConflict(choice: 'reload' | 'saveAsNew' | 'cancel'): Promise<void> {
    if (!this.active() || this.conflict() === null || this.saving() || this.resolving()) return;
    if (choice === 'cancel') { this.conflict.set(null); return; }
    const generation = this.generation;
    this.resolving.set(true);
    try {
      if (choice === 'reload') { await this.refresh(true, true); return; }
      // Keep every live signal while the user-authorized read obtains the latest story ETag.
      const fresh = await this.detail.refetch({ userAction: true });
      if (!this.current(generation)) return;
      if (fresh === null) { this.error.set(this.detail.error()); return; }
      if (!fresh.nextActions.includes('edit_story')) { this.invalidState.set(true); this.showError(this.mapper.fromProblem({ code: 'episode_invalid_state' }, 409, null)); return; }
      await this.put(fresh.currentStory?.etag ?? '');
    } finally {
      if (this.current(generation)) this.resolving.set(false);
    }
  }

  private async put(etag: string): Promise<void> {
    if (!this.active() || this.id === null || this.saving()) return;
    if (!etag || !/^"sv-[^"]+"$/.test(etag)) {
      this.showError(this.mapper.fromProblem({ code: 'if_match_required' }, 428, null)); return;
    }
    const generation = this.generation, id = this.id;
    const sent = this.version(), draft = this.buildDraft(), sentJson = serialize(draft);
    // Fence background reads started before this write. While saving, invalidations never adopt content.
    this.readGeneration++; this.saving.set(true); this.clearErrors();
    try {
      const snapshot = await this.repo.save(id, etag, toContent(draft), { userAction: true });
      if (!this.current(generation)) return;
      this.readGeneration++;
      this.snapshot.set(snapshot); this.baselineJson.set(sentJson); this.savedVersion.set(sent);
      if (this.version() === sent) this.dirtyJson.set(sentJson);
      this.savedAt.set(Date.now()); this.conflict.set(null);
    } catch (cause) {
      if (!this.current(generation)) return;
      const error = this.showError(cause);
      if (error.status === 412) {
        const server = this.detail.episode()?.currentStory ?? this.snapshot();
        this.conflict.set({ serverEtag: server?.etag ?? etag, serverVersion: server?.version ?? 0 });
      } else if (error.status === 409 && error.code === 'episode_invalid_state') {
        this.invalidState.set(true);
        await this.detail.refetch();
      }
    } finally {
      if (this.current(generation)) this.saving.set(false);
    }
  }

  private async refresh(userAction: boolean, replaceDraft: boolean): Promise<void> {
    if (!this.active()) return;
    const generation = this.generation, read = ++this.readGeneration;
    const fresh = await this.detail.refetch(userAction ? { userAction: true } : undefined);
    if (!this.current(generation) || read !== this.readGeneration) return;
    if (fresh === null) { this.error.set(this.detail.error()); return; }
    if (this.saving() || (!replaceDraft && (this.hasUnsavedChanges() || this.conflict() !== null))) return;
    this.adopt(fresh.currentStory); this.invalidState.set(false); this.clearErrors();
  }

  private adopt(snapshot: StorySnapshot | null): void {
    this.snapshot.set(snapshot);
    if (snapshot === null) { this.clearDraft(); return; }
    const draft = fromContent(snapshot.content);
    this.title.set(draft.title); this.hook.set(draft.hook.text); this.ending.set(draft.ending.text);
    this.hookKey.set(draft.hook.segKey); this.endingKey.set(draft.ending.segKey);
    this.hookKeywords.set(draft.hook.keywords); this.endingKeywords.set(draft.ending.keywords);
    this.hookFacts = draft.hook.factRefs; this.endingFacts = draft.ending.factRefs;
    this.paragraphs.set(draft.paragraphs.map((paragraph) => ({ segKey: paragraph.segKey, beat: paragraph.beat,
      text: signal(paragraph.text), keywords: signal(paragraph.keywords), factRefs: paragraph.factRefs,
    })));
    this.meta.set({ title: draft.title, description: draft.description, tags: draft.tags, contentWarnings: draft.contentWarnings, structure: draft.structure });
    const baseline = serialize(draft);
    this.baselineJson.set(baseline); this.dirtyJson.set(baseline); this.savedVersion.set(this.version());
    this.conflict.set(null); this.savedAt.set(null);
    window.clearTimeout(this.dirtyTimer);
    this.dirtyTimer = undefined;
  }

  private buildDraft(): StoryDraft {
    const meta = this.meta();
    const segment = (segKey: string, text: string, keywords: readonly string[], factRefs: readonly unknown[]): StorySegment => ({ segKey, text, keywords, factRefs });
    return {
      title: this.title(), hook: segment(this.hookKey(), this.hook(), this.hookKeywords(), this.hookFacts),
      paragraphs: this.paragraphs().map((paragraph) => ({ beat: paragraph.beat, ...segment(paragraph.segKey, paragraph.text(), paragraph.keywords(), paragraph.factRefs) })),
      ending: segment(this.endingKey(), this.ending(), this.endingKeywords(), this.endingFacts),
      description: meta.description, tags: meta.tags, contentWarnings: meta.contentWarnings, structure: meta.structure,
    };
  }

  private keywordSignal(segKey: string): WritableSignal<readonly string[]> | undefined {
    if (segKey === this.hookKey()) return this.hookKeywords;
    if (segKey === this.endingKey()) return this.endingKeywords;
    return this.paragraphs().find((paragraph) => paragraph.segKey === segKey)?.keywords;
  }

  private edited(): void {
    this.version.update((version) => version + 1); this.savedAt.set(null); this.clearErrors(); this.keywordLimitHit.set(null);
    window.clearTimeout(this.dirtyTimer);
    const generation = this.generation;
    this.dirtyTimer = window.setTimeout(() => {
      this.dirtyTimer = undefined;
      if (this.current(generation)) this.dirtyJson.set(serialize(this.buildDraft()));
    }, 150);
  }

  private showError(cause: unknown): AppError {
    const error = isAppError(cause) ? cause : this.mapper.from(cause);
    this.error.set(error);
    if (error.status === 428) {
      this.formErrors.set(['errors.unknown_error']);
      if (isDevMode()) console.warn('if_match_required', error.traceId ?? '-');
      return error;
    }
    if (error.status !== 400 || error.code !== 'story_invalid_content') { this.formErrors.set([this.mapper.messageKey(error)]); return error; }
    const fields: Partial<Record<SegmentAddress, readonly string[]>> = {};
    const form: string[] = [];
    const pointers = new Set(Object.keys(error.fieldErrors));
    const rows = error.problem?.errors;
    if (Array.isArray(rows)) for (const row of rows as readonly unknown[]) {
      if (typeof row !== 'object' || row === null || Array.isArray(row) || !('field' in row)) continue;
      const field: unknown = row['field'];
      if (typeof field === 'string') pointers.add(field);
    }
    for (const pointer of pointers) {
      let address = parseAddress(pointer);
      if (address === null) {
        const paragraph = /^paragraphs\[(0|[1-9]\d*)\](?:\.(?:text|keywords|fact_refs|beat)(?:\[\d+\])?)?$/.exec(pointer);
        if (paragraph?.[1] !== undefined) address = parseAddress(`p:${paragraph[1]}`);
        else if (/^(?:hook|ending)(?:\.(?:text|keywords|fact_refs)(?:\[\d+\])?)?$/.test(pointer)) address = parseAddress(pointer.split('.')[0] ?? '');
        else if (pointer === 'description' || pointer === 'tags' || pointer === 'content_warnings') address = parseAddress('meta');
      }
      const key = Object.hasOwn(error.fieldErrors, pointer) ? this.mapper.fieldMessageKey(error, pointer) : this.mapper.messageKey(error);
      if (address === null || (address.kind === 'paragraph' && (address.index ?? -1) >= this.paragraphs().length)) form.push(key);
      else {
        const target = formatAddress(address.kind, address.index ?? undefined);
        fields[target] = [...(fields[target] ?? []), key];
      }
    }
    this.fieldErrors.set(fields); this.formErrors.set(form.length > 0 || pointers.size === 0 ? [...new Set(form.length > 0 ? form : [this.mapper.messageKey(error)])] : []);
    return error;
  }

  private clearErrors(): void { this.error.set(null); this.fieldErrors.set({}); this.formErrors.set([]); }
  private clearDraft(): void {
    this.snapshot.set(null); this.title.set(''); this.hook.set(''); this.ending.set(''); this.meta.set(EMPTY_META);
    this.paragraphs.set([]); this.hookKeywords.set([]); this.endingKeywords.set([]); this.hookKey.set(''); this.endingKey.set('');
    this.hookFacts = []; this.endingFacts = []; this.baselineJson.set(''); this.dirtyJson.set('');
    this.savedVersion.set(this.version()); this.savedAt.set(null); this.conflict.set(null); this.keywordLimitHit.set(null);
  }
  private teardown(): void {
    this.subscription?.unsubscribe(); this.subscription = null;
    window.clearTimeout(this.dirtyTimer);
    window.clearTimeout(this.invalidationTimer);
    this.dirtyTimer = undefined; this.invalidationTimer = undefined; this.resolving.set(false);
  }
  private active(): boolean { return !this.destroyed && this.session.generation() === this.ownerGeneration; }
  private current(generation: number): boolean { return this.active() && this.generation === generation; }
  private canEdit(): boolean { return this.active() && !this.loading() && this.snapshot() !== null && !this.readOnly() && !this.invalidState(); }
}
