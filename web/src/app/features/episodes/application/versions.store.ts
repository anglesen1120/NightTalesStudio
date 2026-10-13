import { DestroyRef, Injectable, computed, inject, isDevMode, signal } from '@angular/core';
import type { Signal } from '@angular/core';
import { AppErrorMapper } from '@core/errors';
import type { AppError } from '@core/errors';
import { diffWords } from '@shared/util/text-diff';
import type { WordDiff } from '@shared/util/text-diff';
import type { StoryDraftWire } from '../domain/story.model';
import { StoryRepository } from '../domain/story.repository';
import type { StoryVersionDetail, StoryVersionSummary } from '../domain/story-version.model';
import { StoryVersionRepository } from '../domain/story-version.repository';
import { EpisodeDetailStore, isAppError } from './episode-detail.store';
import { StoryEditorStore } from './story-editor.store';

export type VersionSide = 'left' | 'right';
export type RestoreOutcome = 'restored' | 'conflict' | 'needs_reopen' | 'failed' | 'unavailable';
export interface VersionSideError { readonly side: VersionSide; readonly error: AppError }
export interface VersionSelection { readonly left: string | null; readonly right: string | null }

/** The server's version ETag shape (a missing one is the client's own bug → treated as 428). */
const ETAG_PATTERN = /^"sv-[^"]+"$/;

/**
 * Page-scoped orchestration for the versions side panel (README D13: signals only, never provided in root). It owns
 * the list-detail fan-out, the left/right selection with a per-side generation fence, and the restore-as-new-version
 * composition (`GET …/versions/{id}` + the existing `PUT …/story`; there is no restore route — [OPEN] plan.md §10.1).
 */
@Injectable()
export class VersionsStore {
  private readonly versionRepo = inject(StoryVersionRepository);
  private readonly story = inject(StoryRepository);
  private readonly editor = inject(StoryEditorStore);
  private readonly detail = inject(EpisodeDetailStore);
  private readonly mapper = inject(AppErrorMapper);

  private readonly versionList = signal<readonly StoryVersionSummary[]>([]);
  readonly versions: Signal<readonly StoryVersionSummary[]> = this.versionList.asReadonly();
  private readonly loadedState = signal(false);
  readonly loaded: Signal<boolean> = this.loadedState.asReadonly();
  readonly empty = computed(() => this.loadedState() && this.versionList().length === 0);

  private readonly leftIdState = signal<string | null>(null);
  private readonly rightIdState = signal<string | null>(null);
  readonly leftId: Signal<string | null> = this.leftIdState.asReadonly();
  readonly rightId: Signal<string | null> = this.rightIdState.asReadonly();
  private readonly leftState = signal<StoryVersionDetail | null>(null);
  private readonly rightState = signal<StoryVersionDetail | null>(null);
  readonly left: Signal<StoryVersionDetail | null> = this.leftState.asReadonly();
  readonly right: Signal<StoryVersionDetail | null> = this.rightState.asReadonly();

  readonly diff = computed<WordDiff | null>(() => {
    const left = this.leftState();
    const right = this.rightState();
    if (left === null || right === null || this.leftIdState() === this.rightIdState()) return null;
    return diffWords(left.body, right.body);
  });
  readonly sameSelected = computed(() => this.leftIdState() !== null && this.leftIdState() === this.rightIdState());

  readonly loading = signal(false);
  readonly loadingSide = signal<VersionSide | null>(null);
  readonly sideError = signal<VersionSideError | null>(null);
  readonly error = signal<AppError | null>(null);
  readonly restoring = signal(false);
  readonly pendingConflict = signal(false);
  readonly needsReopen = signal(false);
  readonly restoreError = signal<AppError | null>(null);
  private readonly restoredVersionState = signal<number | null>(null);
  /** One-shot completion survives a panel remount while the page-scoped store remains alive. */
  readonly restoredVersion: Signal<number | null> = this.restoredVersionState.asReadonly();

  readonly canRestore = computed(() => {
    const left = this.leftState();
    return left !== null && left.content !== null && this.leftIdState() !== this.rightIdState() &&
      !left.isCurrent && !this.restoring() && !this.editor.readOnly();
  });
  readonly editorDirty: Signal<boolean> = computed(() => this.editor.isDirty());

  private episodeId: string | null = null;
  private destroyed = false;
  private listGeneration = 0;
  private readonly sideGeneration: Record<VersionSide, number> = { left: 0, right: 0 };

  constructor() {
    inject(DestroyRef).onDestroy(() => {
      this.destroyed = true;
      this.listGeneration++;
      this.sideGeneration.left++;
      this.sideGeneration.right++;
    });
  }

  /** Opens the panel for an episode: fetches the list, then the two requested sides (or previous vs current). */
  async open(episodeId: string, ids?: VersionSelection): Promise<void> {
    if (!this.active()) return;
    if (episodeId === this.episodeId && this.loadedState()) return;
    this.episodeId = episodeId;
    this.restoredVersionState.set(null);
    this.versionList.set([]);
    this.loadedState.set(false);
    this.error.set(null);
    this.clearSelection();
    this.listGeneration++;
    this.sideGeneration.left++;
    this.sideGeneration.right++;
    await this.fetchList();
    if (!this.active() || this.episodeId !== episodeId || !this.loadedState()) return;
    const selection = ids ?? this.defaultSelection();
    await Promise.all([this.applySide(selection.left, 'left'), this.applySide(selection.right, 'right')]);
  }

  /** Re-fetches ONE side, keeping the other; a stale response is dropped by the per-side generation fence. */
  async select(versionId: string, side: VersionSide): Promise<void> {
    if (!this.active() || !this.loadedState()) return;
    await this.loadSide(versionId, side);
  }

  async retrySide(side: VersionSide): Promise<void> {
    const versionId = this.sideId(side);
    if (versionId === null) return;
    await this.loadSide(versionId, side);
  }

  async retryList(): Promise<void> {
    if (!this.active() || this.episodeId === null) return;
    await this.fetchList();
    if (!this.active() || !this.loadedState()) return;
    if (this.leftIdState() === null && this.rightIdState() === null) {
      const selection = this.defaultSelection();
      await Promise.all([this.applySide(selection.left, 'left'), this.applySide(selection.right, 'right')]);
    }
  }

  /** Restores the LEFT version as a new version. The caller confirms first; unsaved editor text is discarded. */
  async restoreAsNewVersion(): Promise<RestoreOutcome> {
    const episodeId = this.episodeId;
    const left = this.leftState();
    if (!this.active() || this.restoring() || episodeId === null || left === null || left.content === null ||
        this.leftIdState() === this.rightIdState() || left.isCurrent || this.editor.readOnly()) return 'unavailable';
    const etag = this.editor.snapshot()?.etag ?? '';
    if (!ETAG_PATTERN.test(etag)) { this.warnMissingEtag(); return 'unavailable'; }
    this.prepareRestore();
    return await this.performRestore(episodeId, etag, left.content);
  }

  async resolveConflict(choice: 'reload' | 'saveAsNew' | 'cancel'): Promise<void> {
    const episodeId = this.episodeId;
    if (!this.active() || episodeId === null) return;
    if (choice === 'cancel') { this.pendingConflict.set(false); return; }
    if (this.restoring()) return;
    if (choice === 'reload') {
      this.pendingConflict.set(false);
      this.restoreError.set(null);
      await this.editor.load(episodeId);
      if (!this.current(episodeId)) return;
      await this.refreshListAndSelectCurrent(episodeId);
      return;
    }
    const content = this.leftState()?.content ?? null;
    if (content === null) { this.pendingConflict.set(false); return; }
    const fresh = await this.detail.refetch({ userAction: true });
    if (!this.current(episodeId)) return;
    const etag = fresh?.currentStory?.etag ?? '';
    if (!ETAG_PATTERN.test(etag)) { this.warnMissingEtag(); return; }
    this.pendingConflict.set(false);
    this.restoreError.set(null);
    await this.performRestore(episodeId, etag, content);
  }
  /** Consumed by the currently mounted host only, after its localized snackbar message is ready. */
  consumeRestored(): number | null {
    const version = this.restoredVersionState();
    this.restoredVersionState.set(null);
    return version;
  }

  reset(): void {
    this.episodeId = null;
    this.listGeneration++;
    this.sideGeneration.left++;
    this.sideGeneration.right++;
    this.versionList.set([]);
    this.loadedState.set(false);
    this.loading.set(false);
    this.error.set(null);
    this.restoring.set(false);
    this.restoredVersionState.set(null);
    this.clearSelection();
  }

  private async performRestore(episodeId: string, etag: string, content: StoryDraftWire): Promise<RestoreOutcome> {
    this.restoring.set(true);
    try {
      await this.story.save(episodeId, etag, content, { userAction: true });
      if (!this.current(episodeId)) return 'failed';
      // Reload the editor first: adopt the new snapshot and clear dirty before the list refetch.
      await this.editor.load(episodeId);
      if (!this.current(episodeId)) return 'failed';
      await this.refreshListAndSelectCurrent(episodeId);
      if (!this.current(episodeId)) return 'failed';
      this.restoredVersionState.set(this.editor.snapshot()?.version ?? null);
      return 'restored';
    } catch (cause) {
      if (!this.current(episodeId)) return 'failed';
      const error = isAppError(cause) ? cause : this.mapper.from(cause);
      if (error.status === 412) { this.pendingConflict.set(true); return 'conflict'; }
      if (error.status === 409 && error.code === 'episode_invalid_state') {
        this.needsReopen.set(true);
        await this.detail.refetch();
        return 'needs_reopen';
      }
      if (error.status === 428) { this.warnMissingEtag(error.traceId); return 'failed'; }
      this.restoreError.set(error);
      return 'failed';
    } finally {
      if (this.current(episodeId)) this.restoring.set(false);
    }
  }

  private async refreshListAndSelectCurrent(episodeId: string): Promise<void> {
    await this.fetchList();
    if (!this.current(episodeId) || !this.loadedState()) return;
    const current = this.versionList().find((version) => version.isCurrent);
    if (current !== undefined) await this.loadSide(current.versionId, 'right');
  }

  private async fetchList(): Promise<void> {
    const episodeId = this.episodeId;
    if (!this.active() || episodeId === null) return;
    const generation = ++this.listGeneration;
    this.loading.set(true);
    this.error.set(null);
    try {
      const list = await this.versionRepo.list(episodeId, { userAction: true });
      if (!this.active() || this.episodeId !== episodeId || this.listGeneration !== generation) return;
      this.versionList.set(list);
      this.loadedState.set(true);
    } catch (cause) {
      if (!this.active() || this.episodeId !== episodeId || this.listGeneration !== generation) return;
      this.error.set(isAppError(cause) ? cause : this.mapper.from(cause));
    } finally {
      if (this.active() && this.episodeId === episodeId && this.listGeneration === generation) this.loading.set(false);
    }
  }

  private async loadSide(versionId: string, side: VersionSide): Promise<void> {
    const episodeId = this.episodeId;
    if (!this.active() || episodeId === null) return;
    const generation = ++this.sideGeneration[side];
    this.setSideId(side, versionId);
    this.setSideDetail(side, null);
    this.loadingSide.set(side);
    if (this.sideError()?.side === side) this.sideError.set(null);
    try {
      const detail = await this.versionRepo.detail(episodeId, versionId, { userAction: true });
      if (this.stale(episodeId, side, generation, versionId)) return;
      this.setSideDetail(side, this.withListFlags(detail));
    } catch (cause) {
      if (this.stale(episodeId, side, generation, versionId)) return;
      this.sideError.set({ side, error: isAppError(cause) ? cause : this.mapper.from(cause) });
    } finally {
      if (!this.stale(episodeId, side, generation, versionId) && this.loadingSide() === side) this.loadingSide.set(null);
    }
  }

  /** `approved`/`isCurrent` are only on the list route: the list row is authoritative for the badges and `canRestore`. */
  private withListFlags(detail: StoryVersionDetail): StoryVersionDetail {
    const row = this.versionList().find((version) => version.versionId === detail.versionId);
    return row === undefined ? detail : { ...detail, approved: row.approved, isCurrent: row.isCurrent };
  }

  private async applySide(versionId: string | null, side: VersionSide): Promise<void> {
    if (versionId === null) { this.clearSide(side); return; }
    await this.loadSide(versionId, side);
  }

  /** Latest response wins per side: a dropped id, remounted episode or newer selection invalidates this call. */
  private stale(episodeId: string, side: VersionSide, generation: number, versionId: string): boolean {
    return !this.active() || this.episodeId !== episodeId || this.sideGeneration[side] !== generation || this.sideId(side) !== versionId;
  }

  private defaultSelection(): VersionSelection {
    const list = this.versionList();
    if (list.length === 0) return { left: null, right: null };
    const currentIndex = list.findIndex((version) => version.isCurrent);
    const current = currentIndex >= 0 ? list[currentIndex] : list[0];
    if (current === undefined) return { left: null, right: null };
    const previous = list[list.indexOf(current) + 1];
    return { left: previous?.versionId ?? null, right: current.versionId };
  }

  private prepareRestore(): void {
    this.restoreError.set(null);
    this.pendingConflict.set(false);
    this.needsReopen.set(false);
  }

  private clearSelection(): void {
    this.leftIdState.set(null);
    this.rightIdState.set(null);
    this.leftState.set(null);
    this.rightState.set(null);
    this.loadingSide.set(null);
    this.sideError.set(null);
    this.pendingConflict.set(false);
    this.needsReopen.set(false);
    this.restoreError.set(null);
  }

  private clearSide(side: VersionSide): void {
    this.setSideId(side, null);
    this.setSideDetail(side, null);
    if (this.sideError()?.side === side) this.sideError.set(null);
  }

  private setSideId(side: VersionSide, versionId: string | null): void {
    if (side === 'left') this.leftIdState.set(versionId); else this.rightIdState.set(versionId);
  }

  private sideId(side: VersionSide): string | null {
    return side === 'left' ? this.leftIdState() : this.rightIdState();
  }

  private setSideDetail(side: VersionSide, detail: StoryVersionDetail | null): void {
    if (side === 'left') this.leftState.set(detail); else this.rightState.set(detail);
  }

  private warnMissingEtag(traceId?: string): void {
    if (isDevMode()) console.warn('if_match_required', traceId ?? '-');
  }

  private active(): boolean { return !this.destroyed; }
  private current(episodeId: string): boolean { return this.active() && this.episodeId === episodeId; }
}
