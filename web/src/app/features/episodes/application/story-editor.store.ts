import { DestroyRef, Injectable, computed, effect, inject, isDevMode, signal } from '@angular/core';
import type { Signal, WritableSignal } from '@angular/core';
import { AppErrorMapper } from '@core/errors';
import type { AppError } from '@core/errors';
import { IdempotencyKeys } from '@core/http';
import type { IdempotencyIntent } from '@core/http';
import { RealtimeService } from '@core/realtime';
import type { RealtimeSubscription } from '@core/realtime';
import { SessionStore } from '@core/session';
import { addressForParagraph, parseAddress, parsePointerAddress, remapAfterInsert, remapAfterRemove, remapAfterReorder } from '@shared/util/segment-address';
import type { SegmentAddress } from '@shared/util/segment-address';
import { fromContent, serialize, toContent } from '../domain/story-codec';
import type { QualityHint, StoryDraft, StoryMetadata, StorySegment, StorySnapshot } from '../domain/story.model';
import type { KeywordFlag, SegmentAnnotation } from '../domain/annotation.model';
import { diffSegmentAddresses, mergeAnnotations } from './annotation-merge';
import { applyMove, applyRemove, insertAt } from './paragraph-structure';
import { mapKeywordsFlagged } from '../data-access/keyword-flags.mapper';
import { StoryRepository } from '../domain/story.repository';
import { EpisodesRepository } from '../domain/episodes.repository';
import { countTokens } from '../domain/token-count';
import { EpisodeDetailStore, isAppError } from './episode-detail.store';

export interface ParagraphState {
  readonly segKey: string;
  readonly beat: string;
  readonly text: WritableSignal<string>;
  readonly keywords: WritableSignal<readonly string[]>;
  readonly tokens: Signal<number>;
  readonly factRefs: readonly unknown[];
}
const EMPTY_META: StoryMetadata = { title: '', description: '', tags: [], contentWarnings: [], structure: null };
const EMPTY_HINTS: readonly QualityHint[] = Object.freeze([]);
/** README C9 length bands, counted in segments (hook + paragraphs + ending) exactly as the backend validator counts. */
const TIER_SEGMENTS: Readonly<Record<string, readonly [number, number]>> = Object.freeze({ short: [4, 5], standard: [20, 32], long: [40, 60] });
/** The undo buffer holds one paragraph for this long; the snackbar action shares the window. */
export const UNDO_WINDOW_MS = 10_000;

/** A deleted paragraph and where it goes back. `text` is kept as removed: an edit made after the delete is not re-applied. */
export interface RemovedParagraph { readonly paragraph: ParagraphState; readonly index: number }

/** Single page-scoped writer. ETags belong to the last adopted/saved story, not a dirty background read. */
@Injectable()
export class StoryEditorStore {
  private readonly repo = inject(StoryRepository);
  private readonly episodes = inject(EpisodesRepository);
  private readonly keys = inject(IdempotencyKeys);
  private readonly detail = inject(EpisodeDetailStore);
  private readonly mapper = inject(AppErrorMapper);
  private readonly realtime = inject(RealtimeService);
  private readonly session = inject(SessionStore);
  private readonly ownerGeneration = this.session.generation();
  readonly loading = signal(true);
  readonly generating = signal(false);
  readonly generationError = signal<AppError | null>(null);
  readonly canGenerate = computed(() => this.active() && !this.loading() && !this.generating() &&
    this.detail.detail()?.currentStory === null && this.detail.nextActions().includes('generate_story'));
  readonly error = signal<AppError | null>(null);
  private readError: AppError | null = null;
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
  private readonly baselineValues = new Map<string, string | readonly string[]>();
  private readonly changedFields = new Set<string>();
  private readonly changedCount = signal(0);
  readonly saving = signal(false);
  private readonly resolving = signal(false);
  readonly savedAt = signal<number | null>(null);
  readonly conflict = signal<true | null>(null);
  /** Segments that differ between my draft and the server's story; `[]` when the server content is unknown. */
  readonly conflictDiff = signal<readonly SegmentAddress[]>([]);
  /** The single user-authorised "save mine as new" attempt itself came back 412: the dialog shows its busy variant. */
  readonly conflictBusy = signal(false);
  /** Non-modal "changed elsewhere" notice: a background read found a different ETag while the draft is dirty. */
  readonly remoteChange = signal<{ readonly etag: string; readonly diff: readonly SegmentAddress[] } | null>(null);
  readonly fieldErrors = signal<Readonly<Partial<Record<SegmentAddress, readonly string[]>>>>({});
  readonly formErrors = signal<readonly string[]>([]);
  readonly keywordLimitHit = signal<string | null>(null);
  readonly invalidState = signal(false);
  /** The undo buffer (one paragraph) and its 10 s window; `removed()` never survives a load, an adopt or a destroy. */
  private readonly removedState = signal<RemovedParagraph | null>(null);
  readonly removed = this.removedState.asReadonly();
  /** A reorder or a delete is an edit `changedCount` cannot see (values did not change): drives Save and the guard. */
  private readonly structuralChanged = signal(false);
  readonly dirty = computed(() => this.snapshot() !== null && (this.changedCount() > 0 || this.structuralChanged()));
  /** `snapshot().qualityHints` re-keyed to the local paragraph order after a move/delete/undo; `null` at the baseline. */
  private readonly hintOverrides = signal<readonly QualityHint[] | null>(null);
  readonly hints = computed<readonly QualityHint[]>(() => this.hintOverrides() ?? this.snapshot()?.qualityHints ?? EMPTY_HINTS);
  /** `409 keywords_flagged` chips; cleared only by a successful save/load/adopt, never by a local edit (WR6). */
  readonly keywordFlags = signal<readonly KeywordFlag[]>([]);
  readonly videoBlocked = computed(() => this.keywordFlags().length > 0);
  /**
   * Server hints merged with the keyword flags (pure, deterministic): the saved hint set, never the draft text, so an
   * edit cannot re-key or re-sort the annotations under the user's cursor.
   */
  readonly annotations = computed<readonly SegmentAnnotation[]>(() => mergeAnnotations(this.hints(), this.keywordFlags()));
  readonly annotationCount = computed(() => this.annotations().length);
  /** Set once per successful save response (after the response snapshot is applied); `null` on load/adopt/clear. */
  readonly annotationAnnouncement = signal<{ readonly count: number; readonly sequence: number } | null>(null);
  /** Advisory only (D9/D18): the client never blocks a save the server's validator would report. */
  readonly structureWarning = computed(() => {
    if (this.snapshot() === null) return false;
    const band = TIER_SEGMENTS[this.detail.detail()?.lengthTier ?? ''];
    if (band === undefined) return false;
    const segments = this.paragraphs().length + 2;
    return segments < band[0] || segments > band[1];
  });
  readonly readOnly = computed(() => !this.detail.nextActions().includes('edit_story'));
  readonly saveDisabled = computed(() => !this.active() || this.loading() || this.saving() || this.resolving() || this.readOnly() || this.invalidState() || (this.changedCount() === 0 && !this.structuralChanged()));
  readonly titleTokens = computed(() => countTokens(this.title()));
  readonly hookTokens = computed(() => countTokens(this.hook()));
  readonly endingTokens = computed(() => countTokens(this.ending()));
  private readonly metaDescription = computed(() => this.meta().description);
  readonly metaTokens = computed(() => countTokens(this.metaDescription()));
  readonly segmentTokens = computed<ReadonlyMap<SegmentAddress, number>>(() => {
    const tokens = new Map<SegmentAddress, number>([['title', this.titleTokens()], ['hook', this.hookTokens()]]);
    this.paragraphs().forEach((paragraph, index) => tokens.set(addressForParagraph(index), paragraph.tokens()));
    tokens.set('ending', this.endingTokens());
    tokens.set('meta', this.metaTokens());
    return tokens;
  });
  readonly totalTokens = computed(() => [...this.segmentTokens().values()].reduce((sum, value) => sum + value, 0));
  private hookFacts: readonly unknown[] = [];
  private endingFacts: readonly unknown[] = [];
  private id: string | null = null;
  private generateIntent: IdempotencyIntent | null = null;
  private generation = 0;
  private readGeneration = 0;
  private destroyed = false;
  private subscription: RealtimeSubscription | null = null;
  private dirtyTimer: number | undefined;
  private invalidationTimer: number | undefined;
  private removedTimer: number | undefined;
  /** segKeys of the last adopted/saved paragraph order: the baseline a structural change is compared with. */
  private baselineKeys: readonly string[] = [];
  /** `p:<i>`-keyed hints of the paragraph that sits in the undo buffer, hidden until it comes back. */
  private bufferedHints: readonly QualityHint[] = [];
  /** Monotonic counter so a live region re-announces even when the count is unchanged across saves. */
  private announcementSequence = 0;
  /** The ETag already surfaced (or dismissed) as a remote change: a repeat never re-raises the same one. */
  private announcedEtag: string | null = null;

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
    const intent = id === this.id ? this.generateIntent : null;
    this.teardown(); this.clearDraft(); this.id = id; this.generateIntent = intent;
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

  async generateStory(): Promise<void> {
    if (!this.canGenerate() || this.id === null) return;
    const generation = this.generation, id = this.id;
    // An ambiguous outcome keeps its key for an explicit retry, never an automatic POST.
    const intent = this.generateIntent?.isSettled === false ? this.generateIntent : this.keys.begin('story.generate');
    this.generateIntent = intent;
    this.generating.set(true); this.generationError.set(null);
    try {
      await this.episodes.generateStory(id, intent);
      if (!this.current(generation)) return;
      intent.settle();
      await this.refresh(true, false);
    } catch (cause) {
      if (!this.current(generation)) return;
      const error = isAppError(cause) ? cause : this.mapper.from(cause);
      if (error.kind !== 'network' && error.status !== 0 && error.status < 500) intent.settle();
      if (error.code === 'story_generation_in_progress') {
        await this.refresh(true, false);
      } else {
        this.generationError.set(error);
        if (error.code === 'episode_invalid_state') await this.refresh(true, false);
      }
    } finally {
      if (this.current(generation)) this.generating.set(false);
    }
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
    this.trackChange(segKey, text); this.edited();
  }

  setSegmentText(kind: 'title' | 'hook' | 'ending', text: string): void {
    if (!this.canEdit()) return;
    this[kind].set(text);
    if (kind === 'title') this.meta.update((meta) => ({ ...meta, title: text }));
    this.trackChange(kind === 'title' ? 'title' : kind === 'hook' ? this.hookKey() : this.endingKey(), text); this.edited();
  }

  addKeyword(segKey: string, keyword: string): void {
    const keywords = this.keywordSignal(segKey);
    if (!this.canEdit() || !keywords) return;
    if (keywords().length >= 2) { this.keywordLimitHit.set(segKey); return; }
    keywords.update((values) => [...values, keyword]); this.trackChange(`${segKey}:keywords`, keywords()); this.edited();
  }

  updateKeyword(segKey: string, index: number, keyword: string): void {
    const keywords = this.keywordSignal(segKey);
    if (!this.canEdit() || !keywords || !Number.isInteger(index) || index < 0 || index >= keywords().length) return;
    keywords.update((values) => values.map((value, i) => i === index ? keyword : value)); this.trackChange(`${segKey}:keywords`, keywords()); this.edited();
  }

  removeKeyword(segKey: string, index: number): void {
    const keywords = this.keywordSignal(segKey);
    if (!this.canEdit() || !keywords || !Number.isInteger(index) || index < 0 || index >= keywords().length) return;
    if (keywords().length <= 1) { this.keywordLimitHit.set(segKey); return; }
    keywords.update((values) => values.filter((_, i) => i !== index)); this.trackChange(`${segKey}:keywords`, keywords()); this.edited();
  }

  /**
   * The ONE write path for `409 keywords_flagged` flags (WR6): replaces the set, ignores calls with no snapshot or
   * after destroy/session change, caps the list, and never rolls back a local edit.
   */
  setKeywordFlags(flags: readonly KeywordFlag[]): void {
    if (!this.active() || this.snapshot() === null) return;
    this.keywordFlags.set(flags.slice(0, 100));
  }

  /**
   * `409 keywords_flagged` handling shared with the approve/render flow (10.5.6-T1): maps the problem's `errors[]` rows
   * (validated in `mapKeywordsFlagged`, malformed rows dropped) onto the exact flagged chips. Returns `true` when the
   * error is that problem and a live draft took the flags, so the caller adds no generic toast of its own; the draft and
   * its ETag are never touched (WR6).
   */
  applyKeywordsFlaggedProblem(error: AppError): boolean {
    if (error.status !== 409 || error.code !== 'keywords_flagged' || !this.active() || this.snapshot() === null) return false;
    this.setKeywordFlags(mapKeywordsFlagged(error.problem?.errors));
    return true;
  }

  setMeta(patch: Partial<StoryMetadata>): void {
    if (!this.canEdit()) return;
    // structure is opaque/read-only and cannot be changed by presentation.
    const { title, description, tags, contentWarnings } = patch;
    this.meta.update((meta) => ({ ...meta,
      ...(title === undefined ? {} : { title }), ...(description === undefined ? {} : { description }),
      ...(tags === undefined ? {} : { tags: [...new Set(tags)] }), ...(contentWarnings === undefined ? {} : { contentWarnings: [...new Set(contentWarnings)] }),
    }));
    if (title !== undefined) { this.title.set(title); this.trackChange('title', title); }
    const meta = this.meta();
    if (description !== undefined) this.trackChange('description', meta.description);
    if (tags !== undefined) this.trackChange('tags', meta.tags);
    if (contentWarnings !== undefined) this.trackChange('contentWarnings', meta.contentWarnings);
    this.edited();
  }

  /** Template helpers (aria-disabled at the ends, never `disabled`, so the buttons keep their place in the tab order). */
  readonly canMoveUp = (index: number): boolean => this.canEdit() && index > 0 && index < this.paragraphs().length;
  readonly canMoveDown = (index: number): boolean => this.canEdit() && index >= 0 && index < this.paragraphs().length - 1;

  moveUp(index: number): boolean {
    return this.moveTo(index, index - 1);
  }

  moveDown(index: number): boolean {
    return this.moveTo(index, index + 1);
  }

  /** True only when the order changed; a drop on itself is not dirty (the value signals stay untouched). */
  moveTo(from: number, to: number): boolean {
    if (!this.canEdit() || !Number.isInteger(from) || !Number.isInteger(to)) return false;
    const count = this.paragraphs().length;
    if (from < 0 || from >= count) return false;
    const target = to < 0 ? 0 : to > count - 1 ? count - 1 : to;
    if (target === from) return false;
    this.paragraphs.set(applyMove(this.paragraphs(), from, target));
    this.remapAddresses('move', from, target);
    this.markStructural();
    return true;
  }

  /** Buffer one paragraph for 10 s; a second delete replaces the buffer (the first deletion is committed from then on). */
  removeParagraph(index: number): boolean {
    if (!this.canEdit()) return false;
    const paragraphs = this.paragraphs();
    const paragraph = paragraphs[index];
    if (!Number.isInteger(index) || index < 0 || index >= paragraphs.length || paragraph === undefined) return false;
    this.removedState.set({ paragraph, index });
    this.paragraphs.set(applyRemove(paragraphs, index));
    this.remapAddresses('remove', index, index);
    this.markStructural();
    this.startUndoTimer();
    return true;
  }

  /** Re-insert the buffered paragraph at its original index (clamped) with its text as removed; `null` when nothing is buffered. */
  undoRemove(): number | null {
    const removed = this.removedState();
    if (removed === null || !this.canEdit()) return null;
    const index = Math.min(removed.index, this.paragraphs().length);
    this.paragraphs.set(insertAt(this.paragraphs(), index, removed.paragraph));
    this.remapAddresses('insert', index, index);
    this.clearRemoved();
    this.markStructural();
    return index;
  }

  /** Synchronously back to the last adopted/saved baseline, so `hasUnsavedChanges()` is false (the user chose Leave). */
  discardDraft(): void {
    this.clearRemoved();
    const snapshot = this.snapshot();
    if (snapshot === null) { this.clearDraft(); return; }
    this.adopt(snapshot);
  }

  async saveVersion(): Promise<void> {
    if (!this.canEdit() || this.saving() || this.resolving() || !this.hasUnsavedChanges()) return;
    await this.put(this.snapshot()?.etag ?? '');
  }

  async resolveConflict(choice: 'reload' | 'saveAsNew' | 'cancel'): Promise<void> {
    if (!this.active() || (this.conflict() === null && this.remoteChange() === null) || this.saving() || this.resolving()) return;
    if (choice === 'cancel') {
      // Dismissing the notice remembers its ETag, so the same one never re-opens; a newer one will.
      this.conflict.set(null); this.conflictDiff.set([]); this.conflictBusy.set(false); this.remoteChange.set(null);
      return;
    }
    const generation = this.generation;
    this.resolving.set(true);
    try {
      if (choice === 'reload') { await this.refresh(true, true); return; }
      // Keep every live signal while the user-authorized read obtains the latest story ETag (one read, one PUT, no loop).
      const fresh = await this.detail.refetch({ userAction: true });
      if (!this.current(generation)) return;
      if (fresh === null) { this.conflict.set(null); this.remoteChange.set(null); this.error.set(this.detail.error()); return; }
      if (!fresh.nextActions.includes('edit_story')) { this.conflict.set(null); this.remoteChange.set(null); this.invalidState.set(true); this.showError(this.mapper.fromProblem({ code: 'episode_invalid_state' }, 409, null)); return; }
      this.invalidState.set(false);
      this.conflictDiff.set(this.diffAgainst(fresh.currentStory));
      await this.put(fresh.currentStory?.etag ?? '', { retry: true });
    } finally {
      if (this.current(generation)) this.resolving.set(false);
    }
  }

  private async put(etag: string, options: { readonly retry?: boolean } = {}): Promise<void> {
    if (!this.active() || this.id === null || this.saving()) return;
    if (!etag || !/^"sv-[^"]+"$/.test(etag)) {
      this.conflict.set(null); this.showError(this.mapper.fromProblem({ code: 'if_match_required' }, 428, null)); return;
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
      this.recordBaseline(draft);
      for (const [key, value] of this.draftValues(this.buildDraft())) this.trackChange(key, value);
      if (this.version() === sent) this.dirtyJson.set(sentJson);
      this.savedAt.set(Date.now()); this.conflict.set(null); this.conflictDiff.set([]); this.conflictBusy.set(false);
      this.remoteChange.set(null); this.announcedEtag = null;
      // The server recomputed the feedback for the content it just acknowledged: the hints arrive in the response and
      // the keyword flags are no longer in play, so a local edit never has to clear them itself.
      this.keywordFlags.set([]);
      this.announcementSequence++;
      this.annotationAnnouncement.set({ count: this.annotations().length, sequence: this.announcementSequence });
    } catch (cause) {
      if (!this.current(generation)) return;
      const error = this.showError(cause);
      if (error.status === 412) {
        this.conflict.set(true);
        if (options.retry === true) {
          // The single user-authorised re-attempt also lost the race: keep the dialog, flag the busy variant, stop here.
          this.conflictBusy.set(true);
        } else {
          this.conflictBusy.set(false);
          await this.loadConflictDiff(generation);
        }
      } else {
        this.conflict.set(null);
        if (error.status === 409 && error.code === 'episode_invalid_state') {
          this.invalidState.set(true);
          const fresh = await this.detail.refetch();
          if (this.current(generation) && fresh?.nextActions.includes('edit_story')) this.invalidState.set(false);
        } else {
          // `409 keywords_flagged`: the flagged chips and the annotation panel are the feedback (the existing
          // `showError` copy `errors.keywords_flagged` stays as the form error); the draft and ETag are untouched.
          this.applyKeywordsFlaggedProblem(error);
        }
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
    if (fresh === null) { this.readError = this.detail.error(); this.error.set(this.readError); return; }
    if (fresh.nextActions.includes('edit_story')) this.invalidState.set(false);
    // A reload is user-authorised; every other read must not adopt while a write or a resolve is in flight.
    if (this.saving() || (this.resolving() && !replaceDraft)) return;
    if (!replaceDraft) {
      // The dialog owns the flow: an invalidation never replaces the draft, clears the conflict or re-raises a notice.
      if (this.conflict() !== null) return;
      if (this.hasUnsavedChanges()) {
        // Dirty draft: never adopt. If the server moved on, surface the non-modal notice once per new ETag.
        this.raiseRemoteChange(fresh.currentStory);
        return;
      }
      if (fresh.currentStory?.etag === this.snapshot()?.etag) {
        if (this.readError !== null && this.error() === this.readError) { this.error.set(null); this.readError = null; }
        return;
      }
    }
    this.adopt(fresh.currentStory); this.clearErrors();
  }

  /** One user-authorised read to describe the conflict (never adopts): the segments my draft and the server disagree on. */
  private async loadConflictDiff(generation: number): Promise<void> {
    const read = ++this.readGeneration;
    const fresh = await this.detail.refetch({ userAction: true });
    if (!this.current(generation) || read !== this.readGeneration) return;
    this.conflictDiff.set(this.diffAgainst(fresh?.currentStory ?? null));
  }

  /** Segments whose text/keywords differ between the live draft and a server story; `[]` when the story is unknown. */
  private diffAgainst(snapshot: StorySnapshot | null): readonly SegmentAddress[] {
    return snapshot === null ? [] : diffSegmentAddresses(this.buildDraft(), fromContent(snapshot.content));
  }

  /** Raise the "changed elsewhere" notice once per distinct ETag; a dismissed (or already shown) ETag never re-raises. */
  private raiseRemoteChange(story: StorySnapshot | null): void {
    if (story === null || story.etag === this.snapshot()?.etag || story.etag === this.announcedEtag) return;
    this.announcedEtag = story.etag;
    this.remoteChange.set({ etag: story.etag, diff: this.diffAgainst(story) });
  }

  private adopt(snapshot: StorySnapshot | null): void {
    this.snapshot.set(snapshot);
    this.clearRemoved();
    if (snapshot === null) { this.clearDraft(); return; }
    const draft = fromContent(snapshot.content);
    this.title.set(draft.title); this.hook.set(draft.hook.text); this.ending.set(draft.ending.text);
    this.hookKey.set(draft.hook.segKey); this.endingKey.set(draft.ending.segKey);
    this.hookKeywords.set(draft.hook.keywords); this.endingKeywords.set(draft.ending.keywords);
    this.hookFacts = draft.hook.factRefs; this.endingFacts = draft.ending.factRefs;
    this.paragraphs.set(draft.paragraphs.map((paragraph) => {
      const text = signal(paragraph.text);
      return { segKey: paragraph.segKey, beat: paragraph.beat, text,
        tokens: computed(() => countTokens(text())), keywords: signal(paragraph.keywords), factRefs: paragraph.factRefs };
    }));
    this.meta.set({ title: draft.title, description: draft.description, tags: draft.tags, contentWarnings: draft.contentWarnings, structure: draft.structure });
    const baseline = serialize(draft);
    this.baselineJson.set(baseline); this.dirtyJson.set(baseline); this.savedVersion.set(this.version()); this.recordBaseline(draft);
    this.conflict.set(null); this.savedAt.set(null);
    // An adopt replaces the draft with server content: the old flags, notice and announcement no longer describe it.
    this.keywordFlags.set([]); this.conflictDiff.set([]); this.conflictBusy.set(false);
    this.remoteChange.set(null); this.announcedEtag = null; this.annotationAnnouncement.set(null);
    window.clearTimeout(this.dirtyTimer);
    this.dirtyTimer = undefined;
  }

  /**
   * Rebuild every `p:<i>`-keyed address after a structural change: a hint follows the same text through a move, a
   * delete and an undo, and the removed paragraph's own hints stay hidden while it sits in the buffer. Runs once per
   * structural change, never per keystroke.
   */
  private remapAddresses(kind: 'move' | 'remove' | 'insert', from: number, to: number): void {
    const map = (address: SegmentAddress): SegmentAddress | null => {
      if (kind === 'move') return remapAfterReorder([address], from, to)[0] ?? address;
      return kind === 'remove' ? remapAfterRemove(address, from) : remapAfterInsert(address, from);
    };
    const remapHint = (hint: QualityHint): QualityHint | null => {
      const segment = hint.segment;
      if (segment === null) return hint;
      const target = map(segment);
      return target === null ? null : target === segment ? hint : { ...hint, segment: target };
    };
    const hints: QualityHint[] = [];
    const dropped: QualityHint[] = [];
    for (const hint of this.hints()) {
      const mapped = remapHint(hint);
      if (mapped === null) dropped.push(hint); else hints.push(mapped);
    }
    // A buffered paragraph comes back at its own removed index (`undoRemove`), so its hints stay keyed to that index:
    // a move reorders the live paragraphs only and never re-keys the buffer; `insert` (undo) returns them untouched.
    if (kind === 'remove') this.bufferedHints = dropped;
    if (kind === 'insert') { hints.push(...this.bufferedHints); this.bufferedHints = []; }
    this.hintOverrides.set(hints);
  }

  /** The order moved away from (or back to) the baseline order: re-derive the flag and commit the change as an edit. */
  private markStructural(): void {
    this.recomputeStructural();
    this.edited();
  }

  /** True when the current paragraph keys differ from the baseline order (a reorder or a delete). */
  private recomputeStructural(): void {
    const keys = this.paragraphs().map((paragraph) => paragraph.segKey);
    this.structuralChanged.set(keys.length !== this.baselineKeys.length || keys.some((key, index) => key !== this.baselineKeys[index]));
  }

  private startUndoTimer(): void {
    window.clearTimeout(this.removedTimer);
    const generation = this.generation;
    this.removedTimer = window.setTimeout(() => {
      this.removedTimer = undefined;
      if (this.current(generation)) this.clearRemoved();
    }, UNDO_WINDOW_MS);
  }

  /** Drop the undo buffer and its timer (destroy, load, adopt, session change, `discardDraft` or the window closing). */
  private clearRemoved(): void {
    window.clearTimeout(this.removedTimer);
    this.removedTimer = undefined;
    this.removedState.set(null);
    this.bufferedHints = [];
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

  private draftValues(draft: StoryDraft): ReadonlyMap<string, string | readonly string[]> {
    const values = new Map<string, string | readonly string[]>([
      ['title', draft.title], ['description', draft.description], ['tags', draft.tags], ['contentWarnings', draft.contentWarnings],
      [draft.hook.segKey, draft.hook.text], [`${draft.hook.segKey}:keywords`, draft.hook.keywords],
      [draft.ending.segKey, draft.ending.text], [`${draft.ending.segKey}:keywords`, draft.ending.keywords],
    ]);
    for (const paragraph of draft.paragraphs) {
      values.set(paragraph.segKey, paragraph.text); values.set(`${paragraph.segKey}:keywords`, paragraph.keywords);
    }
    return values;
  }

  private recordBaseline(draft: StoryDraft): void {
    this.baselineValues.clear();
    for (const [key, value] of this.draftValues(draft)) this.baselineValues.set(key, value);
    this.changedFields.clear(); this.changedCount.set(0);
    this.baselineKeys = draft.paragraphs.map((paragraph) => paragraph.segKey);
    // The server's hints describe the order it just acknowledged: the local remap starts over from them.
    this.hintOverrides.set(null);
    this.recomputeStructural();
  }

  private trackChange(key: string, value: string | readonly string[]): void {
    const baseline = this.baselineValues.get(key);
    const equal = typeof value === 'string' ? value === baseline : Array.isArray(baseline) && value.length === baseline.length && value.every((item, index) => item === baseline[index]);
    if (equal) this.changedFields.delete(key); else this.changedFields.add(key);
    this.changedCount.set(this.changedFields.size);
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
    this.readError = null; this.error.set(error);
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
      const target = parsePointerAddress(pointer);
      const address = target === null ? null : parseAddress(target);
      const key = Object.hasOwn(error.fieldErrors, pointer) ? this.mapper.fieldMessageKey(error, pointer) : this.mapper.messageKey(error);
      if (address === null || (address.kind === 'paragraph' && (address.index ?? -1) >= this.paragraphs().length)) form.push(key);
      else if (target !== null) fields[target] = [...new Set([...(fields[target] ?? []), key])];
    }
    this.fieldErrors.set(fields); this.formErrors.set(form.length > 0 || pointers.size === 0 ? [...new Set(form.length > 0 ? form : [this.mapper.messageKey(error)])] : []);
    return error;
  }

  private clearErrors(): void { this.readError = null; this.error.set(null); this.fieldErrors.set({}); this.formErrors.set([]); }
  private clearDraft(): void {
    this.snapshot.set(null); this.title.set(''); this.hook.set(''); this.ending.set(''); this.meta.set(EMPTY_META);
    this.paragraphs.set([]); this.hookKeywords.set([]); this.endingKeywords.set([]); this.hookKey.set(''); this.endingKey.set('');
    this.hookFacts = []; this.endingFacts = []; this.baselineJson.set(''); this.dirtyJson.set('');
    this.baselineValues.clear(); this.changedFields.clear(); this.changedCount.set(0);
    this.savedVersion.set(this.version()); this.savedAt.set(null); this.conflict.set(null); this.keywordLimitHit.set(null);
    this.keywordFlags.set([]); this.conflictDiff.set([]); this.conflictBusy.set(false);
    this.remoteChange.set(null); this.announcedEtag = null; this.annotationAnnouncement.set(null);
    this.clearRemoved(); this.hintOverrides.set(null); this.baselineKeys = []; this.structuralChanged.set(false);
  }
  private teardown(): void {
    this.generateIntent = null; this.generating.set(false); this.generationError.set(null);
    this.subscription?.unsubscribe(); this.subscription = null;
    window.clearTimeout(this.dirtyTimer);
    window.clearTimeout(this.invalidationTimer);
    this.dirtyTimer = undefined; this.invalidationTimer = undefined; this.resolving.set(false);
    this.clearRemoved();
  }
  private active(): boolean { return !this.destroyed && this.session.generation() === this.ownerGeneration; }
  private current(generation: number): boolean { return this.active() && this.generation === generation; }
  private canEdit(): boolean { return this.active() && !this.loading() && this.snapshot() !== null && !this.readOnly() && !this.invalidState(); }
}
