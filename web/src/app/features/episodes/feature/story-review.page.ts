import { ChangeDetectionStrategy, Component, DestroyRef, ViewContainerRef, afterRenderEffect, computed, effect, inject, signal, untracked, viewChild } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { ActivatedRoute } from '@angular/router';
import { TranslocoPipe, provideTranslocoScope } from '@jsverse/transloco';
import { AppErrorMapper } from '@core/errors';
import { ConfirmDialog, ConflictDialog, EmptyState, ErrorState, FormErrorAlert, LoadingState, PageHeader, StatusChip } from '@shared/ui';
import { formatAddress, parseAddress } from '@shared/util/segment-address';
import type { SegmentAddress } from '@shared/util/segment-address';
import { firstValueFrom } from 'rxjs';
import { combineLatest } from 'rxjs';
import { EpisodeDetailStore } from '../application/episode-detail.store';
import { StoryEditorStore } from '../application/story-editor.store';
import { StoryEditorComponent } from '../ui/story-editor.component';
import { buildStoryEditorViewModel } from '../ui/story-editor.view-model';
import type { StoryEditorIntent } from '../ui/story-editor.view-model';

const ACTION_KEYS: Readonly<Record<string, string>> = {
  generate_story: 'episodes.editor.action.generate_story',
  approve_story: 'episodes.editor.action.approve_story',
  reopen_story: 'episodes.editor.action.reopen_story',
  render: 'episodes.editor.action.render',
  retry_render: 'episodes.editor.action.retry_render',
  cancel: 'episodes.editor.action.cancel',
  approve_video: 'episodes.editor.action.approve_video',
  reject_video: 'episodes.editor.action.reject_video',
  publish: 'episodes.editor.action.publish',
  download: 'episodes.editor.action.download',
};

@Component({
  selector: 'nts-story-review-page',
  imports: [TranslocoPipe, EmptyState, ErrorState, FormErrorAlert, LoadingState, PageHeader, StatusChip, StoryEditorComponent],
  providers: [EpisodeDetailStore, StoryEditorStore, provideTranslocoScope('episodes', 'errors')],
  templateUrl: './story-review.page.html',
  styleUrl: './story-review.page.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class StoryReviewPage {
  protected readonly detailStore = inject(EpisodeDetailStore);
  protected readonly store = inject(StoryEditorStore);
  private readonly route = inject(ActivatedRoute);
  private readonly mapper = inject(AppErrorMapper);
  private readonly conflictDialog = inject(ConflictDialog);
  private readonly confirmDialog = inject(ConfirmDialog);
  private readonly viewContainerRef = inject(ViewContainerRef);
  private readonly destroyRef = inject(DestroyRef);
  private readonly editor = viewChild(StoryEditorComponent);
  private readonly routeState = toSignal(combineLatest([this.route.paramMap, this.route.queryParamMap]), {
    initialValue: [this.route.snapshot.paramMap, this.route.snapshot.queryParamMap] as const,
  });
  private readonly focusRequest = signal<{ readonly address: SegmentAddress; readonly sequence: number } | null>(null);
  private lastEpisodeId: string | null = null;
  private lastFocusSequence = -1;
  private focusSequence = 0;
  private conflictOpen = false;

  protected readonly viewModel = computed(() => buildStoryEditorViewModel({
    title: this.store.title(), hook: this.store.hook(), ending: this.store.ending(),
    hookKey: this.store.hookKey(), endingKey: this.store.endingKey(),
    hookKeywords: this.store.hookKeywords(), endingKeywords: this.store.endingKeywords(),
    paragraphs: this.store.paragraphs().map((paragraph) => ({ segKey: paragraph.segKey, beat: paragraph.beat, text: paragraph.text(), keywords: paragraph.keywords() })),
    meta: this.store.meta(), segmentTokens: this.store.segmentTokens(), totalTokens: this.store.totalTokens(),
    fieldErrors: this.store.fieldErrors(), formErrors: this.store.formErrors(), readOnly: this.store.readOnly(),
    keywordLimitHit: this.store.keywordLimitHit(), isDirty: this.store.isDirty(), saving: this.store.saving(),
    savedAt: this.store.savedAt(), error: this.store.error() !== null, saveDisabled: this.store.saveDisabled(),
  }));
  protected readonly loading = computed(() => this.store.loading() || this.store.generating() ||
    (this.store.snapshot() === null && this.detailStore.loading()));
  protected readonly fatalError = computed(() => this.store.snapshot() === null ? this.store.error() ?? this.detailStore.error() : null);
  protected readonly errorKey = computed(() => {
    const error = this.fatalError();
    return error === null ? 'errors.unknown_error' : this.mapper.messageKey(error);
  });
  protected readonly traceId = computed(() => this.fatalError()?.traceId);
  protected readonly episode = this.detailStore.detail;
  protected readonly allowedActions = computed(() => this.detailStore.nextActions().filter((action) => action !== 'edit_story' && Object.hasOwn(ACTION_KEYS, action)));

  constructor() {
    effect(() => {
      const [params, query] = this.routeState();
      const id = params.get('id') ?? '';
      if (id !== this.lastEpisodeId) {
        this.lastEpisodeId = id;
        untracked(() => { void this.store.load(id); });
      }
      const rawAddress = query.get('segment');
      const parsed = rawAddress === null ? null : parseAddress(rawAddress);
      this.focusRequest.set(parsed === null ? null : {
        address: formatAddress(parsed.kind, parsed.index ?? undefined),
        sequence: ++this.focusSequence,
      });
    });
    effect(() => {
      if (this.store.readOnly() || this.store.conflict() === null || this.conflictOpen) return;
      untracked(() => { void this.openConflict(); });
    });
    afterRenderEffect(() => {
      const request = this.focusRequest();
      const editor = this.editor();
      if (request === null || editor === undefined || this.loading() || request.sequence === this.lastFocusSequence) return;
      if (editor.focusSegment(request.address)) this.lastFocusSequence = request.sequence;
    });
  }

  protected actionKey(action: string): string {
    return ACTION_KEYS[action] ?? 'episodes.editor.action.unavailable';
  }

  protected retry(): void {
    if (this.lastEpisodeId !== null) void this.store.load(this.lastEpisodeId);
  }

  protected handleIntent(intent: StoryEditorIntent): void {
    switch (intent.kind) {
      case 'editText':
        if (intent.segKey === 'title' || intent.segKey === 'hook' || intent.segKey === 'ending') this.store.setSegmentText(intent.segKey, intent.text);
        else if (intent.segKey === 'meta') this.store.setMeta({ description: intent.text });
        else this.store.setText(intent.segKey, intent.text);
        break;
      case 'editKeyword':
        if (intent.op === 'add') this.store.addKeyword(intent.segKey, intent.value ?? '');
        else if (intent.op === 'update' && intent.index !== undefined) this.store.updateKeyword(intent.segKey, intent.index, intent.value ?? '');
        else if (intent.op === 'remove' && intent.index !== undefined) this.store.removeKeyword(intent.segKey, intent.index);
        break;
      case 'save': void this.store.saveVersion(); break;
      case 'openSegment': this.editor()?.focusSegment(intent.address); break;
      case 'resolveConflict': void this.store.resolveConflict(intent.choice); break;
    }
  }

  private async openConflict(): Promise<void> {
    if (this.conflictOpen || this.store.readOnly() || this.store.conflict() === null) return;
    this.conflictOpen = true;
    try {
      while (!this.destroyRef.destroyed && !this.store.readOnly() && this.store.conflict() !== null) {
        const dialog = this.conflictDialog.open({
          messageKey: 'episodes.conflict.body',
          saveAsNewLabel: 'errors.conflict.saveAsNew',
          traceId: this.store.error()?.traceId,
        }, { viewContainerRef: this.viewContainerRef });
        const choice = (await firstValueFrom(dialog.afterClosed())) ?? 'cancel';
        if (this.destroyRef.destroyed) return;
        if (choice === 'reload') {
          const confirm = this.confirmDialog.confirm({
            titleKey: 'episodes.conflict.reloadConfirmTitle',
            bodyKey: 'episodes.conflict.reloadConfirmBody',
            confirmLabelKey: 'episodes.conflict.reload',
            destructive: true,
          }, { viewContainerRef: this.viewContainerRef });
          const accepted = (await firstValueFrom(confirm.afterClosed())) ?? false;
          if (this.destroyRef.destroyed) return;
          await this.store.resolveConflict(accepted ? 'reload' : 'cancel');
          if (!accepted) return;
        } else {
          await this.store.resolveConflict(choice);
          if (choice === 'cancel') return;
        }
      }
    } finally {
      this.conflictOpen = false;
    }
  }
}
