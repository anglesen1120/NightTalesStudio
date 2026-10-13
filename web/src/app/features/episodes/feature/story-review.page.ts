import { ChangeDetectionStrategy, Component, DestroyRef, ElementRef, ViewContainerRef, afterRenderEffect, computed, effect, inject, isDevMode, signal, untracked, viewChild } from '@angular/core';
import type { Signal } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { ActivatedRoute, Router } from '@angular/router';
import { MatSnackBar } from '@angular/material/snack-bar';
import { TranslocoPipe, TranslocoService, provideTranslocoScope } from '@jsverse/transloco';
import { AppErrorMapper } from '@core/errors';
import { UnsavedWorkRegistry } from '@core/shell/unsaved-work';
import { ConfirmDialog, ConflictDialog, EmptyState, ErrorState, FormErrorAlert, LoadingState, PageHeader, StatusChip } from '@shared/ui';
import { addressForParagraph, formatAddress, parseAddress } from '@shared/util/segment-address';
import type { ParsedAddress, SegmentAddress } from '@shared/util/segment-address';
import { EMPTY, catchError, combineLatest, firstValueFrom, map } from 'rxjs';
import { EpisodeDetailStore } from '../application/episode-detail.store';
import { StoryEditorStore, UNDO_WINDOW_MS } from '../application/story-editor.store';
import { VersionsStore } from '../application/versions.store';
import { VersionsPanelHostComponent } from './versions-panel-host.component';
import { StoryEditorComponent } from '../ui/story-editor.component';
import { buildStoryEditorViewModel } from '../ui/story-editor.view-model';
import type { SegmentTexts, StoryEditorIntent, StoryEditorViewModel } from '../ui/story-editor.view-model';
import { EMPTY_ANNOTATION_VIEWS, buildAnnotationViews } from '../ui/annotation.view-model';
import type { AnnotationViewOptions, AnnotationViews } from '../ui/annotation.view-model';

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

/**
 * Which body copy the conflict dialog opens with. `busy` is the second consecutive 412 (the save-as-new attempt itself
 * lost the race again) and `reloadFailed` a reload that errored with the conflict still standing; both keep the three
 * actions, so the recovered state is one more user click away.
 */
type ConflictVariant = 'body' | 'busy' | 'reloadFailed';

const CONFLICT_MESSAGE_KEYS: Readonly<Record<ConflictVariant, string>> = {
  body: 'episodes.conflict.body',
  busy: 'episodes.conflict.busyBody',
  reloadFailed: 'episodes.conflict.reloadFailed',
};

@Component({
  selector: 'nts-story-review-page',
  imports: [TranslocoPipe, EmptyState, ErrorState, FormErrorAlert, LoadingState, PageHeader, StatusChip, StoryEditorComponent, VersionsPanelHostComponent],
  providers: [EpisodeDetailStore, StoryEditorStore, VersionsStore, provideTranslocoScope('episodes', 'errors', 'quality')],
  templateUrl: './story-review.page.html',
  styleUrl: './story-review.page.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class StoryReviewPage {
  protected readonly detailStore = inject(EpisodeDetailStore);
  protected readonly store = inject(StoryEditorStore);
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);
  private readonly mapper = inject(AppErrorMapper);
  private readonly conflictDialog = inject(ConflictDialog);
  private readonly confirmDialog = inject(ConfirmDialog);
  private readonly snack = inject(MatSnackBar);
  private readonly transloco = inject(TranslocoService);
  private readonly unsavedWork = inject(UnsavedWorkRegistry);
  private readonly viewContainerRef = inject(ViewContainerRef);
  private readonly destroyRef = inject(DestroyRef);
  private readonly editor = viewChild(StoryEditorComponent);
  // `read: ElementRef`: `#editorHost` sits on a component element, so a bare string query would resolve to the
  // component instance and the focus restore below needs the host DOM node.
  private readonly editorHost = viewChild<HTMLElement, ElementRef<HTMLElement>>('editorHost', { read: ElementRef });
  private readonly versionsToggle = viewChild<ElementRef<HTMLButtonElement>>('versionsToggle');
  private readonly restoreVersionsFocus = signal(false);
  private readonly restoreSaveFocus = signal(false);
  protected readonly conflictDialogOpen = signal(false);
  private readonly routeState = toSignal(combineLatest([this.route.paramMap, this.route.queryParamMap]), {
    initialValue: [this.route.snapshot.paramMap, this.route.snapshot.queryParamMap] as const,
  });
  /** Annotation copy is translated in the active language, so views rebuild on a language switch. */
  private readonly language = toSignal(this.transloco.langChanges$, { initialValue: this.transloco.getActiveLang() });
  /**
   * Subscribing to the quality scope is both the load trigger and the readiness signal: until it lands a hint's
   * `quality.<ruleId>` key cannot resolve. A failed load leaves the signal `null` for good (the loader retries on the
   * next activation), so the page degrades to no annotations instead of throwing out of the template.
   */
  private readonly qualityScope = toSignal(
    this.transloco.selectTranslation('quality').pipe(catchError(() => EMPTY)),
    { initialValue: null },
  );
  private readonly focusRequest = signal<{ readonly address: SegmentAddress; readonly sequence: number } | null>(null);
  private lastEpisodeId: string | null = null;
  private lastFocusSequence = -1;
  private lastSegmentFocusKey: string | null = null;
  private focusSequence = 0;
  private leavePrompt: Promise<boolean> | null = null;

  private previousViewModel: StoryEditorViewModel | undefined;
  protected readonly segmentTexts = computed<SegmentTexts>(() => new Map<SegmentAddress, Signal<string>>([
    ['hook', this.store.hook],
    ...this.store.paragraphs().map((paragraph, index) => [addressForParagraph(index), paragraph.text] as const),
    ['ending', this.store.ending],
  ]));
  /**
   * One options object for the builder. `hasKey` asks the live service: `translate` hands a key back when the active
   * language cannot resolve it — a scope that has not loaded, or an entry the language lacks — so an unknown rule id
   * never reaches the UI as if it were copy. `warnUnknown` logs the key alone (D19), once per distinct key.
   */
  private readonly annotationOptions: AnnotationViewOptions = {
    hasKey: (key) => this.transloco.translate(key) !== key,
    warnUnknown: (key) => this.warnUnknownAnnotationKey(key),
  };
  private readonly warnedAnnotationKeys = new Set<string>();
  private previousAnnotationViews: AnnotationViews | undefined;
  /**
   * Annotations depend on the saved hint set and the keyword flags, never on draft text, so a keystroke cannot rebuild
   * them. Until the quality scope has loaded no hint key can resolve, so the page shows nothing rather than a rule id
   * — and passing no `previous` then keeps the builder from memoising a fallback it would have to revise.
   */
  protected readonly annotationViews = computed<AnnotationViews>(() => {
    if (this.qualityScope() === null) return EMPTY_ANNOTATION_VIEWS;
    const next = buildAnnotationViews(this.store.annotations(), this.annotationOptions, this.previousAnnotationViews);
    this.previousAnnotationViews = next;
    return next;
  });
  private announcedSequence: number | null = null;
  private announceWithTrailingSpace = false;
  /**
   * Already-translated text for the editor's single polite live region; `null` renders nothing at all. A live region
   * announces a *change*, and two saves can report the same count — the copy would be identical and the region would
   * stay mute. Each new sequence therefore alternates an invisible trailing space: the spoken text is unchanged, the
   * region still mutates, and no timer is involved. Only a new sequence flips it, so a recomputed view is stable.
   */
  protected readonly announcement = computed<string | null>(() => {
    this.language();
    const announcement = this.store.annotationAnnouncement();
    if (announcement === null) return null;
    if (announcement.sequence !== this.announcedSequence) {
      this.announcedSequence = announcement.sequence;
      this.announceWithTrailingSpace = !this.announceWithTrailingSpace;
    }
    const text = announcement.count > 0
      ? this.transloco.translate('annotations.announce', { count: announcement.count }, 'episodes')
      : this.transloco.translate('annotations.announceNone', {}, 'episodes');
    return this.announceWithTrailingSpace ? `${text} ` : text;
  });
  protected readonly remoteNoticeLabels = computed<readonly string[]>(() => {
    this.language();
    const remote = this.store.remoteChange();
    return remote === null ? [] : this.diffLabels(remote.diff);
  });
  private readonly saveErrorKey = computed(() => {
    const error = this.store.error();
    // A cancelled conflict keeps the draft, not an active save failure announcement.
    if (error === null || (error.status === 412 && this.store.conflict() === null)) return null;
    return this.mapper.messageKey(error);
  });
  protected readonly viewModel = computed(() => {
    const next = buildStoryEditorViewModel({
      title: this.store.title(), hook: this.store.hook(), ending: this.store.ending(),
      hookKey: this.store.hookKey(), endingKey: this.store.endingKey(),
      hookKeywords: this.store.hookKeywords(), endingKeywords: this.store.endingKeywords(),
      paragraphs: this.store.paragraphs().map((paragraph) => ({ segKey: paragraph.segKey, beat: paragraph.beat, text: paragraph.text(), keywords: paragraph.keywords() })),
      meta: this.store.meta(), metaTokens: this.store.metaTokens(), segmentTokens: this.store.segmentTokens(), totalTokens: this.store.totalTokens(),
      fieldErrors: this.store.fieldErrors(), formErrors: this.store.formErrors(), readOnly: this.store.readOnly(),
      keywordLimitHit: this.store.keywordLimitHit(), isDirty: this.store.isDirty(), saving: this.store.saving(),
      savedAt: this.store.savedAt(), errorKey: this.saveErrorKey(), saveDisabled: this.store.saveDisabled(),
      structureWarning: this.store.structureWarning(),
    }, this.previousViewModel);
    this.previousViewModel = next;
    return next;
  });
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
  protected readonly versionsOpen = computed(() => this.routeState()[1].get('panel') === 'versions');
  protected readonly versionCount = computed(() => this.store.snapshot()?.version ?? null);
  protected readonly versionIds = computed(() => {
    const query = this.routeState()[1];
    const left = query.get('left'), right = query.get('right');
    const uuid = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;
    return { left: left !== null && uuid.test(left) ? left : null, right: right !== null && uuid.test(right) ? right : null };
  });

  protected toggleVersions(): void {
    if (this.versionsOpen()) this.closeVersions();
    else void this.router.navigate([], { relativeTo: this.route, queryParams: { panel: 'versions' }, queryParamsHandling: 'merge', replaceUrl: true });
  }

  protected closeVersions(): void {
    void this.router.navigate([], { relativeTo: this.route, queryParams: { panel: null, left: null, right: null }, queryParamsHandling: 'merge', replaceUrl: true }).then((navigated) => {
      if (navigated && !this.destroyRef.destroyed) this.restoreVersionsFocus.set(true);
    });
  }

  protected changeVersions(ids: { left: string | null; right: string | null }): void {
    const query = this.routeState()[1];
    if (query.get('left') === ids.left && query.get('right') === ids.right) return;
    void this.router.navigate([], { relativeTo: this.route, queryParams: ids, queryParamsHandling: 'merge', replaceUrl: true });
  }

  constructor() {
    effect(() => {
      const [params, query] = this.routeState();
      const id = params.get('id') ?? '';
      if (id !== this.lastEpisodeId) {
        this.lastEpisodeId = id;
        untracked(() => { void this.store.load(id); });
      }
      const rawAddress = query.get('segment');
      const focusKey = id + ':' + (rawAddress ?? '');
      if (focusKey === this.lastSegmentFocusKey) return;
      this.lastSegmentFocusKey = focusKey;
      const parsed = rawAddress === null ? null : parseAddress(rawAddress);
      this.focusRequest.set(parsed === null ? null : {
        address: formatAddress(parsed.kind, parsed.index ?? undefined),
        sequence: ++this.focusSequence,
      });
    });
    effect(() => {
      if (this.store.readOnly() || this.store.conflict() === null) return;
      // A 412 is recorded while its own request is still finishing: the store reads the conflicting diff right after.
      // Opening now would show the "nothing differs" copy for a genuine diff, so the dialog waits for the write to end.
      if (this.store.saving()) return;
      // Read untracked: the loop writes this flag, and a tracked read would make the effect depend on its own state.
      if (untracked(() => this.conflictDialogOpen())) return;
      untracked(() => { void this.openConflict(); });
    });
    afterRenderEffect(() => {
      const request = this.focusRequest();
      const editor = this.editor();
      if (request === null || editor === undefined || this.loading() || request.sequence === this.lastFocusSequence) return;
      if (editor.focusSegment(request.address)) this.lastFocusSequence = request.sequence;
    });
    // One handler for both the route guard and the shell logout: both go through `confirmLeave()`, so a logout that is
    // already asking a question is never asked again by the route deactivation that follows it.
    this.destroyRef.onDestroy(this.unsavedWork.register({
      hasUnsaved: () => this.store.hasUnsavedChanges(),
      // A terminal `SessionEnded` discards through this handler before its navigation, so the guard never prompts.
      discard: () => { if (this.store.hasUnsavedChanges()) this.store.discardDraft(); },
      confirmLeave: () => this.confirmLeave(),
    }));
    // The listener is registered while the draft is dirty (a cheap flag, never a serialisation per keystroke) and the
    // check inside it is the synchronous one, so a keystroke inside the 150 ms dirty debounce still prompts.
    effect((onCleanup) => {
      if (!this.store.dirty()) return;
      const listener = (event: BeforeUnloadEvent): void => {
        if (!this.store.hasUnsavedChanges()) return;
        event.preventDefault();
        event.returnValue = '';
      };
      window.addEventListener('beforeunload', listener);
      onCleanup(() => window.removeEventListener('beforeunload', listener));
    });
    effect((onCleanup) => {
      const removed = this.store.removed();
      if (removed === null) return;
      const ref = this.snack.open(this.transloco.translate('editor.deleted', {}, 'episodes'), this.transloco.translate('editor.undo', {}, 'episodes'), { duration: UNDO_WINDOW_MS });
      // `onAction`, not `afterDismissed`: the action click emits synchronously, while `afterDismissed` waits for the
      // exit animation — a click in the last frames of the 10 s window would otherwise race the store timer that
      // clears the buffer. `MatSnackBarRef` completes `onAction` without emitting on every other dismissal (the
      // duration timeout, `dismiss()` from this cleanup), so `defaultValue: false` is the "no click" answer; the
      // identity check keeps a superseded snackbar (its animation can outlive the buffer) from undoing a paragraph a
      // later delete holds. `firstValueFrom` rather than a hand-rolled subscription: a component never subscribes by
      // hand (D2/D13).
      void firstValueFrom(ref.onAction().pipe(map(() => true)), { defaultValue: false }).then((clicked) => {
        if (clicked && this.store.removed() === removed) this.undoRemove();
      });
      onCleanup(() => ref.dismiss());
    });
    afterRenderEffect(() => {
      if (!this.restoreVersionsFocus() || this.versionsOpen()) return;
      const toggle = this.versionsToggle();
      if (toggle === undefined) return;
      toggle.nativeElement.focus();
      this.restoreVersionsFocus.set(false);
    });
    // Cancelling a conflict leaves the user mid-draft, so the save control takes focus back (the dialog restores focus
    // to whatever held it before, which is not necessarily where the draft is saved from).
    afterRenderEffect(() => {
      if (!this.restoreSaveFocus()) return;
      const host = this.editorHost();
      if (host === undefined) return;
      this.restoreSaveFocus.set(false);
      host.nativeElement.querySelector<HTMLButtonElement>('.story-save-bar button')?.focus();
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
      case 'moveParagraph': this.store.moveTo(intent.from, intent.to); break;
      case 'removeParagraph': this.removeParagraph(intent.index); break;
    }
  }

  /**
   * The one prompt for leaving a dirty draft (10.5.9-T1). The route guard, the unsaved-work registry and the logout
   * flow all call this, so one in-flight question is shared and the route deactivation that follows a logout never
   * asks a second time. `discardDraft()` runs here — never in the guard — so a cancelled navigation cannot leak an
   * intermediate order into a later save.
   */
  confirmLeave(): Promise<boolean> {
    if (!this.store.hasUnsavedChanges()) return Promise.resolve(true);
    this.leavePrompt ??= this.askToLeave().finally(() => { this.leavePrompt = null; });
    return this.leavePrompt;
  }

  private async askToLeave(): Promise<boolean> {
    const dialog = this.confirmDialog.confirm({
      titleKey: 'episodes.dirty.title',
      bodyKey: 'episodes.dirty.body',
      confirmLabelKey: 'episodes.dirty.leave',
      cancelLabelKey: 'episodes.dirty.stay',
      destructive: true,
    }, { viewContainerRef: this.viewContainerRef });
    const leave = (await firstValueFrom(dialog.afterClosed())) ?? false;
    if (leave && !this.destroyRef.destroyed) this.store.discardDraft();
    return leave;
  }

  /** Delete, then hand focus to the paragraph that takes its place (else the previous one) after the next render. */
  private removeParagraph(index: number): void {
    if (!this.store.removeParagraph(index)) return;
    const remaining = this.store.paragraphs().length;
    if (remaining === 0) { this.focusRequest.set(null); return; }
    this.focusRequest.set({ address: addressForParagraph(Math.min(index, remaining - 1)), sequence: ++this.focusSequence });
  }

  /** Undo: the restored paragraph takes focus after the next render. */
  private undoRemove(): void {
    const index = this.store.undoRemove();
    if (index === null) return;
    this.focusRequest.set({ address: addressForParagraph(index), sequence: ++this.focusSequence });
  }

  /** The changed-elsewhere notice is not a dialog: reload still confirms (it discards the draft), the others act at once. */
  protected reloadRemoteNotice(): void {
    void this.reloadAfterConfirm();
  }

  protected saveMineRemoteNotice(): void {
    void this.store.resolveConflict('saveAsNew');
  }

  protected dismissRemoteNotice(): void {
    void this.store.resolveConflict('cancel');
  }

  /**
   * The conflict dialog (10.5.10-T1). One dialog at a time, and after every awaited step the page re-checks that it is
   * still alive: a route change during any of them must not open a dialog or touch the store.
   *
   * The loop exists so the dialog can come back carrying the outcome of the user's own last action — a declined
   * confirm returns to it with the draft untouched, a second 412 shows the busy body, a failed reload its retry body.
   * It never re-opens on its own: every iteration is entered only after the user closed the previous dialog.
   */
  private async openConflict(): Promise<void> {
    if (this.conflictDialogOpen() || this.store.readOnly() || this.store.conflict() === null) return;
    this.conflictDialogOpen.set(true);
    try {
      let variant: ConflictVariant = this.store.conflictBusy() ? 'busy' : 'body';
      while (!this.destroyRef.destroyed && !this.store.readOnly() && this.store.conflict() !== null) {
        const dialog = this.conflictDialog.open({
          messageKey: CONFLICT_MESSAGE_KEYS[variant],
          detailHeadingKey: 'episodes.conflict.diffHeading',
          detailTexts: this.diffLabels(this.store.conflictDiff()),
          saveAsNewLabel: 'errors.conflict.saveAsNew',
          traceId: this.store.error()?.traceId,
        }, { viewContainerRef: this.viewContainerRef });
        const choice = (await firstValueFrom(dialog.afterClosed())) ?? 'cancel';
        if (this.destroyRef.destroyed) return;
        variant = 'body';
        if (choice === 'cancel') {
          await this.store.resolveConflict('cancel');
          if (this.destroyRef.destroyed) return;
          this.restoreSaveFocus.set(true);
          return;
        }
        if (choice === 'reload') {
          // Declining the confirm is not a decision about the conflict: the dialog is the state the user came back to.
          if (!await this.reloadAfterConfirm()) continue;
          if (this.destroyRef.destroyed) return;
          if (this.store.conflict() === null) return;
          variant = this.store.error() === null ? 'busy' : 'reloadFailed';
          continue;
        }
        // Save as new: exactly one attempt. A second 412 is not retried from here — the dialog re-opens in the busy
        // variant and waits for the next explicit click, which is where the user decides what to do with the race.
        await this.store.resolveConflict('saveAsNew');
        if (this.destroyRef.destroyed) return;
        if (this.store.conflict() === null) return;
        variant = this.store.conflictBusy() ? 'busy' : 'body';
      }
    } finally {
      this.conflictDialogOpen.set(false);
    }
  }

  /** Reload replaces the unsaved text, so it always goes through the destructive confirm — from the dialog and the notice alike. */
  private async reloadAfterConfirm(): Promise<boolean> {
    const confirm = this.confirmDialog.confirm({
      titleKey: 'episodes.conflict.reloadConfirmTitle',
      bodyKey: 'episodes.conflict.reloadConfirmBody',
      confirmLabelKey: 'episodes.conflict.reload',
      destructive: true,
    }, { viewContainerRef: this.viewContainerRef });
    const accepted = (await firstValueFrom(confirm.afterClosed())) ?? false;
    if (this.destroyRef.destroyed) return false;
    if (accepted) await this.store.resolveConflict('reload');
    return accepted;
  }

  /** The diff and the changed-elsewhere notice name segments exactly like the editor does; an empty diff still says so. */
  private diffLabels(addresses: readonly SegmentAddress[]): readonly string[] {
    if (addresses.length === 0) return [this.transloco.translate('conflict.diffNone', {}, 'episodes')];
    return addresses.map((address) => this.addressLabel(parseAddress(address)));
  }

  private addressLabel(address: ParsedAddress | null): string {
    if (address === null) return this.transloco.translate('annotations.meta', {}, 'episodes');
    switch (address.kind) {
      case 'title': return this.transloco.translate('editor.segment.title', {}, 'episodes');
      case 'hook': return this.transloco.translate('editor.segment.hook', {}, 'episodes');
      case 'paragraph': return `${this.transloco.translate('editor.segment.paragraph', {}, 'episodes')} ${(address.index ?? 0) + 1}`;
      case 'ending': return this.transloco.translate('editor.segment.ending', {}, 'episodes');
      case 'meta': return this.transloco.translate('annotations.meta', {}, 'episodes');
    }
  }

  private warnUnknownAnnotationKey(key: string): void {
    if (!isDevMode() || this.warnedAnnotationKeys.has(key)) return;
    this.warnedAnnotationKeys.add(key);
    // D19: the key alone — never a hint's evidence, never story text.
    console.warn('unknown annotation key', key);
  }
}
