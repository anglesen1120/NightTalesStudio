import { ChangeDetectionStrategy, Component, DestroyRef, ElementRef, ViewContainerRef, afterRenderEffect, computed, effect, inject, isDevMode, signal, untracked, viewChild } from '@angular/core';
import type { Signal } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { MatSnackBar } from '@angular/material/snack-bar';
import { TranslocoPipe, TranslocoService, provideTranslocoScope } from '@jsverse/transloco';
import { AppErrorMapper } from '@core/errors';
import { UnsavedWorkRegistry } from '@core/shell/unsaved-work';
import { ConfirmDialog, ConflictDialog, EmptyState, ErrorState, FormErrorAlert, LoadingState, PageHeader, RetryCountdown, StatusChip } from '@shared/ui';
import { addressForParagraph, formatAddress, parseAddress } from '@shared/util/segment-address';
import type { ParsedAddress, SegmentAddress } from '@shared/util/segment-address';
import { catchError, combineLatest, firstValueFrom, map, of, switchMap } from 'rxjs';
import type { Observable } from 'rxjs';
import { EpisodeDetailStore } from '../application/episode-detail.store';
import { ApprovalStore } from '../application/approval.store';
import { EPISODE_GATE } from '../application/episode-gate';
import type { GateState } from '../domain/episode-gate';
import { ResearchStore } from '../application/research.store';
import { MakeVideoStore } from '../application/make-video.store';
import { RenderTimelineStore } from '../application/render-timeline.store';
import { VideoReviewStore } from '../application/video-review.store';
import { GENERIC_TITLE_KEY, failureCopy } from '../application/render-failure';
import type { EpisodeDetail } from '../domain/episode.model';
import type { FailureCopy } from '../domain/render.model';
import { ApproveDialogComponent } from '../ui/approve-dialog/approve-dialog.component';
import { MakeVideoButtonComponent } from '../ui/make-video-button/make-video-button.component';
import { ReopenDialogComponent } from '../ui/reopen-dialog/reopen-dialog.component';
import { RenderFailureComponent } from '../ui/render-failure/render-failure.component';
import { RenderTimelineComponent } from '../ui/render-timeline/render-timeline.component';
import { StoryEditorStore, UNDO_WINDOW_MS } from '../application/story-editor.store';
import { VideoReviewComponent } from '../ui/video-review/video-review.component';
import { VideoDecisionDialogComponent } from '../ui/video-decisions/video-decision-dialog.component';
import { VersionsStore } from '../application/versions.store';
import { VersionsPanelHostComponent } from './versions-panel-host.component';
import { StoryEditorComponent } from '../ui/story-editor.component';
import type { StoryEditorAnnouncement } from '../ui/story-editor.component';
import { buildStoryEditorViewModel } from '../ui/story-editor.view-model';
import type { SegmentTexts, StoryEditorIntent, StoryEditorViewModel } from '../ui/story-editor.view-model';
import { buildAnnotationViews } from '../ui/annotation.view-model';
import type { AnnotationViewOptions, AnnotationViews } from '../ui/annotation.view-model';
import type { SimilarityLevel } from '../domain/audit.model';
import { ReviewPanelComponent } from '../ui/review-panel.component';
import type { SimilarityHighlightViewModel } from '../ui/review-panel.view-model';
import { RevisedBadgeComponent } from '../ui/revised-badge.component';

const NO_SIMILARITY_HIGHLIGHTS: ReadonlyMap<SegmentAddress, SimilarityLevel> = new Map();

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

/** One scope subtree tagged with the language it was loaded for, so readiness never answers from a previous language. */
interface LoadedScope {
  readonly lang: string;
  readonly scope: unknown;
}

/**
 * A scope is usable only when its subtree carries entries AND it was loaded for the language now active: a subscription
 * restarted by a language switch keeps serving the previous language's object until the new file lands, and resolving a
 * key against that object answers with the key itself, which reads as a missing rule rather than as "not loaded yet".
 */
function isScopeLoaded(loaded: LoadedScope, language: string): boolean {
  return loaded.lang === language && typeof loaded.scope === 'object' && loaded.scope !== null && Object.keys(loaded.scope).length > 0;
}

@Component({
  selector: 'nts-story-review-page',
  imports: [TranslocoPipe, RouterLink, EmptyState, ErrorState, FormErrorAlert, LoadingState, PageHeader, StatusChip, StoryEditorComponent, VersionsPanelHostComponent, ApproveDialogComponent, ReopenDialogComponent, ReviewPanelComponent, RevisedBadgeComponent, RenderTimelineComponent, RenderFailureComponent, MakeVideoButtonComponent, RetryCountdown, VideoReviewComponent, VideoDecisionDialogComponent],
  providers: [EpisodeDetailStore, StoryEditorStore, ApprovalStore, VersionsStore, ResearchStore, RenderTimelineStore, MakeVideoStore, VideoReviewStore, { provide: EPISODE_GATE, useFactory: () => inject(ResearchStore).gate }, provideTranslocoScope('episodes', 'errors', 'quality')],
  templateUrl: './story-review.page.html',
  styleUrl: './story-review.page.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class StoryReviewPage {
  private readonly detailStore = inject(EpisodeDetailStore);
  protected readonly store = inject(StoryEditorStore);
  protected readonly approval = inject(ApprovalStore);
  /** The render timeline of this episode (WD1/F5): component-scoped, so its subscription, tick and baseline die with the page. */
  protected readonly renderStore = inject(RenderTimelineStore);
  /**
   * The "Make video" command (10.6.1-T2). Component-scoped like the timeline, not route-scoped: the held idempotency
   * intent and the in-flight state must die with this page, and the same instance feeds the header button and the
   * failure panel's retry (one store, one intent, one set of disabled reasons).
   */
  protected readonly makeVideo = inject(MakeVideoStore);
  /**
   * The video review of this episode (10.6.2-T1, WD4): component-scoped like the timeline. Its signed URL, its due
   * check and a held reject intent must die with the page, not survive in the route injector the router caches
   * (plan.md §12); the page only opens and releases the view around it.
   */
  protected readonly reviewStore = inject(VideoReviewStore);
  /** Component-owned so the gate and its reads are discarded when this editor is destroyed. */
  private readonly researchGate = inject(EPISODE_GATE);
  private readonly research = inject(ResearchStore);
  private readonly versions = inject(VersionsStore);
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
  private readonly pageHeader = viewChild<PageHeader, ElementRef<HTMLElement>>(PageHeader, { read: ElementRef });
  private readonly reopenTrigger = viewChild<ElementRef<HTMLButtonElement>>('reopenTrigger');
  private approvalTrigger: HTMLElement | null = null;
  private closeApprovalConflict: (() => void) | null = null;
  private savedDetailRefresh: Promise<EpisodeDetail | null> | null = null;
  private readonly restoreApprovalFocus = signal(false);
  protected readonly approveOpen = signal(false);
  protected readonly reopenOpen = signal(false);
  protected readonly approvalSaveHint = computed(() => this.store.dirty() || this.store.saving());
  private readonly pendingApprovalAnnouncement = signal<'approved' | 'reopened' | null>(null);
  protected readonly approvalAnnouncement = signal<StoryEditorAnnouncement | null>(null);
  private approvalAnnouncementSequence = 0;
  private readonly restoreVersionsFocus = signal(false);
  private readonly restoreSaveFocus = signal(false);
  protected readonly conflictDialogOpen = signal(false);
  private readonly routeState = toSignal(combineLatest([this.route.paramMap, this.route.queryParamMap]), {
    initialValue: [this.route.snapshot.paramMap, this.route.snapshot.queryParamMap] as const,
  });
  /** Annotation copy is translated in the active language, so views rebuild on a language switch. */
  private readonly language = toSignal(this.transloco.langChanges$, { initialValue: this.transloco.getActiveLang() });
  /**
   * Subscribing to the quality scope is both the load trigger and the readiness signal: until the *active language's*
   * file lands a hint's `quality.<ruleId>` key cannot resolve, so hints are withheld (keyword flags are not — see
   * `annotationViews`). The path pins the language the emission belongs to, and the catch sits inside the per-language
   * switch, so a failed load leaves that language unready while the next language change retries it — the stream is never
   * completed for the page's life.
   */
  private readonly qualityScope = toSignal(
    this.transloco.langChanges$.pipe(switchMap((lang) => this.loadScope('quality', lang))),
    { initialValue: { lang: this.transloco.getActiveLang(), scope: null } },
  );
  /**
   * The `episodes` scope is loaded lazily by the router-scoped provider; until the active language's file lands, an
   * imperative `translate('…', {}, 'episodes')` echoes the key back. The labels the page builds in TypeScript (the diff
   * lists, the dialog's detail texts) and the announcement wait for `episodesLoaded`, so a raw key never reaches the DOM.
   */
  private readonly episodesScope = toSignal(
    this.transloco.langChanges$.pipe(switchMap((lang) => this.loadScope('episodes', lang))),
    { initialValue: { lang: this.transloco.getActiveLang(), scope: null } },
  );
  private readonly qualityLoaded = computed(() => isScopeLoaded(this.qualityScope(), this.language()));
  private readonly episodesLoaded = computed(() => isScopeLoaded(this.episodesScope(), this.language()));
  /** One language's scope file, tagged with that language (a failed load leaves it unready rather than guessing). */
  private loadScope(scope: string, lang: string): Observable<LoadedScope> {
    return this.transloco.selectTranslation(`${scope}/${lang}`).pipe(
      map((translation): LoadedScope => ({ lang, scope: translation })),
      catchError(() => of<LoadedScope>({ lang, scope: null })),
    );
  }
  private readonly focusRequest = signal<{ readonly address: SegmentAddress; readonly sequence: number } | null>(null);
  private lastEpisodeId: string | null = null;
  /** The episode the review view is currently open for, or `null`; drives the one URL read and its release (WD4). */
  private reviewingEpisodeId: string | null = null;
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
  protected readonly reviewDescriptionId = computed(() => {
    const ids = [
      ...(this.store.driftNote() ? ['story-review-audit-drift'] : []),
      ...(this.store.reviewBlock()?.stale ? ['story-review-audit-stale'] : []),
    ];
    return ids.length === 0 ? null : ids.join(' ');
  });
  /** Snapshot/hint/source signals only: typing never rebuilds the address map. */
  protected readonly similarityHighlights = computed(() =>
    this.store.sourcesExist() && !this.store.similarityUnavailable() && this.store.similarity()?.corpus !== 'past_episodes'
      ? this.store.similarityHighlights() : NO_SIMILARITY_HIGHLIGHTS);
  protected readonly similarityRows = computed<readonly SimilarityHighlightViewModel[]>(() => {
    if (!this.store.sourcesExist() || this.store.similarityUnavailable()) return [];
    const levels = this.similarityHighlights();
    return (this.store.similarity()?.segments ?? []).map(({ segment, share }) => {
      const parsed = segment === 'meta' ? null : parseAddress(segment);
      return {
        address: segment, share, level: levels.get(segment) ?? null,
        addressLabelKey: segment === 'meta' ? 'episodes.annotations.meta' : `episodes.editor.segment.${parsed?.kind ?? 'paragraph'}`,
        addressLabelIndex: parsed?.kind === 'paragraph' && parsed.index !== null ? parsed.index + 1 : null,
        addressText: segment !== 'meta' && parsed === null ? segment : null,
      };
    });
  });
  /**
   * Only a recorded issue missing from the flattened hints proves disagreement. Extra local/checker hints are normal;
   * audit ids are not hint rule ids, so neither total counts nor an invented id mapping can establish a discrepancy.
   */
  protected readonly reviewPartial = computed(() => {
    const review = this.store.reviewBlock();
    if (review === null) return false;
    const hints = this.store.snapshot()?.qualityHints ?? [];
    return this.store.whyIssues().some((issue) => !hints.some((hint) =>
      hint.segment === issue.segment && hint.kind === issue.kind &&
      (hint.kind === 'taste' ? 'advisory' : hint.severity) === issue.severity &&
      hint.evidence === issue.evidence && hint.repairDirection === issue.repairDirection));
  });
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
   * them. A hint's `quality.<ruleId>` key cannot resolve until the quality scope has loaded, so hints are withheld
   * then: rendering them would show the "not translated" fallback for feedback that is perfectly valid. Keyword flags
   * carry `errors.*`/`episodes.*` keys, so a failed (or still pending) quality load must never hide them.
   */
  protected readonly annotationViews = computed<AnnotationViews>(() => {
    // `hasKey` resolves against the active language, so a language switch must rebuild the views; the announcement copy
    // is the one thing that does not follow the language (it stays frozen per sequence — see `announcement`).
    this.language();
    const annotations = this.store.annotations();
    const visible = this.qualityLoaded() ? annotations : annotations.filter((annotation) => annotation.source !== 'hint');
    const next = buildAnnotationViews(visible, this.annotationOptions, this.previousAnnotationViews);
    this.previousAnnotationViews = next;
    return next;
  });
  /** The frozen announcement copy and the sequence it belongs to, so a language switch cannot rewrite what was read. */
  private announcedSequence: number | null = null;
  private announcedText: string | null = null;
  /**
   * Already-translated text for the editor's single polite live region; `null` renders nothing at all. The copy is
   * resolved once, when the announcement sequence changes, and is frozen there: a live region announces a *change*, and
   * a later language switch must not rewrite text a screen reader has already read. It never reads a language signal.
   * `episodesLoaded` is read only so that a scope which lands after the save fills the copy in (never a raw key) — and
   * only while the sequence has no frozen text yet. A new sequence re-creates the announced node (see the editor), so
   * two saves that report the same count still announce — no trailing-space trick, no timer.
   */
  protected readonly announcement = computed<StoryEditorAnnouncement | null>(() => {
    const announcement = this.store.annotationAnnouncement();
    const ready = this.episodesLoaded();
    if (announcement === null) {
      this.announcedSequence = null;
      this.announcedText = null;
      return null;
    }
    if (announcement.sequence !== this.announcedSequence) {
      this.announcedSequence = announcement.sequence;
      this.announcedText = ready ? this.announcementText(announcement.count) : null;
    } else if (this.announcedText === null && ready) {
      this.announcedText = this.announcementText(announcement.count);
    }
    return this.announcedText === null ? null : { text: this.announcedText, sequence: announcement.sequence };
  });
  protected readonly remoteNoticeLabels = computed<readonly string[]>(() => {
    this.language();
    if (!this.episodesLoaded()) return [];
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
  /**
   * The render view of `/episodes/:id` (WD1): a `rendering` episode, and a `failed` one whose job row still exists — the
   * two states the timeline and the failure panel own. Every other status renders the story editor below.
   */
  protected readonly renderView = computed<boolean>(() => {
    const episode = this.episode();
    if (episode === null) return false;
    return episode.status === 'rendering' || (episode.status === 'failed' && episode.renderJob !== null);
  });
  /**
   * The review view of `/episodes/:id` (WD4): `video_review` — the finished cut before the owner decides — and `ready`,
   * the state an approval produces. Both render the same container; `ready` narrows it to the `finalVideo` metadata and
   * the download link (no decisions, **no** publication state — the per-publication half of `ready`/`scheduled`/
   * `published` is 10.7.4-T1's, `plan.md` §10 O-1), so `scheduled` and `published` stay unhandled here on purpose.
   */
  protected readonly reviewView = computed<boolean>(() => this.episode()?.status === 'video_review');
  protected readonly readyView = computed<boolean>(() => this.episode()?.status === 'ready');
  /** The failure panel's copy, resolved to keys only (F7/D9): the server's own prose is never read or rendered. */
  protected readonly renderFailureCopy = computed<FailureCopy>(() => {
    const failure = this.renderStore.failure();
    return failure === null
      ? { titleKey: GENERIC_TITLE_KEY, bodyKeys: [], actionKeys: [] }
      : failureCopy(failure.code, failure.scenes);
  });
  /** The actions a failed render offers: the server's own `nextActions`, unfiltered — 10.6.1-T2 binds the retry. */
  protected readonly renderActions = computed<readonly string[]>(() => this.episode()?.nextActions ?? []);
  /**
   * The one make-video message the view shows (D9: the server's `detail` is never read): the catalogue leaf the store
   * resolved (its `errors.unknown_error` for the 428/422 client-bug answers). A single computed, so one refusal is
   * announced once; the store retires it with its cause (the fixed keyword flags, a finished 429 wait), and the keyword
   * flags themselves live in `StoryEditorStore` (WR6), never here.
   */
  protected readonly makeVideoErrorKey = computed(() => {
    // A refusal describes a command the view that offered it was running. Once the timeline owns the view and the
    // server offers neither render nor retry any more (C4: the 409 whose refetch brought the job in), the banner would
    // describe something the user can no longer do. The failure view keeps it: `retry_render` is still the command there.
    if (this.renderView() && this.makeVideo.labelKey() === null) return null;
    return this.makeVideo.errorKey();
  });
  /** The correlation line a client-bug answer carries (plan U6/`errors.unknown.trace`); `null` when there is none. */
  protected readonly makeVideoTrace = computed(() => {
    const error = this.makeVideo.error();
    return error === null ? null : this.mapper.traceFooter(error);
  });
  /** A refused cancel, mapped to its `errors.<code>` copy; `null` while none is pending. */
  protected readonly cancelErrorKey = computed(() => {
    const error = this.renderStore.cancelError();
    return error === null ? null : this.mapper.messageKey(error);
  });
  /** The read-only research gate (02 F13): `not_applicable` and an unloaded gate are `open`. */
  protected readonly researchGateState = computed<GateState>(() => this.researchGate());
  /** Only the two blocking states disable "Write story"; the server stays the ultimate authority (a 409 on generate). */
  protected readonly storyGateBlocked = computed(() => {
    const state = this.researchGateState();
    return state === 'sheet_not_approved' || state === 'sources_insufficient';
  });
  /** Which half of the blocked copy the hint shows; the link itself opens the research page. */
  protected readonly gateHintKey = computed(() => this.researchGateState() === 'sources_insufficient'
    ? 'episodes.research.gate.sourcesInsufficient'
    : 'episodes.research.gate.sheetNotApproved');
  /**
   * The read-only notice's inert action list: the server's `nextActions` minus the ones this page renders as real
   * controls — `edit_story`/`reopen_story` (the header and the versions panel own them) and `render`/`retry_render`,
   * which 10.6.1-T2 renders as the live `nts-make-video-button` (the header button and the failure panel's retry); a
   * disabled duplicate here would be a second, dead control for the same command.
   */
  protected readonly allowedActions = computed(() => this.detailStore.nextActions().filter((action) =>
    action !== 'edit_story' && action !== 'reopen_story' && action !== 'render' && action !== 'retry_render' &&
    Object.hasOwn(ACTION_KEYS, action)));
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

  protected openApprove(trigger: HTMLElement): void {
    if (!this.approval.canOpenApprove()) return;
    this.approvalTrigger = trigger;
    this.approveOpen.set(true);
  }

  protected openReopen(trigger?: HTMLElement): void {
    if (!this.approval.canReopen()) return;
    this.approvalTrigger = trigger ?? (document.activeElement instanceof HTMLElement ? document.activeElement : null);
    this.reopenOpen.set(true);
  }

  protected closeApprovalDialogs(): void {
    this.approveOpen.set(false);
    this.reopenOpen.set(false);
    this.approval.clearBlocked();
    this.approval.setAccepted(false);
    this.restoreApprovalFocus.set(true);
  }

  /**
   * The review dialog's confirm (10.6.2-T1): one decision, by the mode the store opened. `approve()` and `reject()`
   * write nothing locally — each awaits the authoritative refetch, so the status the page switches on is the server's.
   * The mode is read here rather than passed as an event payload: the store owns which dialog is open, and `null`
   * (a dialog that is already gone) sends nothing at all.
   */
  protected confirmReviewDecision(): void {
    const mode = this.reviewStore.dialog();
    if (mode === 'approve') void this.reviewStore.approve();
    else if (mode === 'reject') void this.reviewStore.reject();
  }

  protected async confirmApproval(intent: 'approve' | 'reopen', episodeId: string): Promise<void> {
    if (this.savedDetailRefresh !== null) await this.savedDetailRefresh;
    if (this.destroyRef.destroyed || this.lastEpisodeId !== episodeId || this.episode()?.id !== episodeId) return;
    if (intent === 'approve' ? !this.approveOpen() : !this.reopenOpen()) return;
    if (intent === 'approve') await this.approval.approve(episodeId);
    else await this.approval.reopen(episodeId);
  }
  constructor() {
    effect(() => {
      const [params, query] = this.routeState();
      const id = params.get('id') ?? '';
      if (id !== this.lastEpisodeId) {
        this.lastEpisodeId = id;
        untracked(() => {
          this.closeApprovalConflict?.();
          this.closeApprovalConflict = null;
          this.approveOpen.set(false);
          this.reopenOpen.set(false);
          this.approvalTrigger = null;
          this.restoreApprovalFocus.set(false);
          this.pendingApprovalAnnouncement.set(null);
          this.approvalAnnouncement.set(null);
          this.approval.reset();
          void this.store.load(id);
          // One component-owned gate load; a return to this editor gets a fresh instance and fresh metadata.
          void this.research.load(id);
          // The render store is armed for every id in this view (its `start` is idempotent per id): a `:id` reuse drops
          // the previous job's baseline, subscription and tick, and a story-editor episode never renders the timeline.
          this.renderStore.start(id);
          // C4: the previous episode's make-video message state — its refusal, its 429 wait — is not about this one.
          this.makeVideo.dismiss();
        });
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
      const hasSources = this.store.snapshot() !== null && this.research.loaded() && this.research.sources().length > 0;
      untracked(() => this.store.setSourcesExist(hasSources));
    });
    /**
     * A `rendering` episode whose job row has not landed yet (the failure-handling row): `LoadingState` plus exactly one
     * background re-read. The effect tracks the *value* of the condition, so the same still-pending state never queues a
     * second read — a fresh occurrence (`false → true`) does, and the refetch's own answer cannot re-trigger it.
     */
    effect(() => {
      if (!this.renderStore.needsRefetch()) return;
      untracked(() => { void this.renderStore.refresh(); });
    });
    /**
     * The review branches' opening read and their due check (WD4/WB3/F10): entering `video_review`/`ready` fetches the
     * signed URL once and arms `visiblePoll`; leaving them releases both. The release is what keeps a `re_render` that
     * ends in another `video_review` for the same id honest — the page instance is reused, so a kept URL would serve
     * the previous render's link and no-op the opening read — and it stops every `video-url` request behind the
     * timeline branch. The comparison is the episode id, not a flag, so a `:id` change re-reads; the effect depends on
     * that id and the two statuses alone (a keystroke or a poll never re-runs it), and both store calls are idempotent.
     */
    effect(() => {
      const episodeId = this.episode()?.id ?? null;
      const open = episodeId !== null && (this.reviewView() || this.readyView());
      untracked(() => {
        const served = open ? episodeId : null;
        if (served === this.reviewingEpisodeId) return;
        if (this.reviewingEpisodeId !== null) this.reviewStore.release();
        this.reviewingEpisodeId = served;
        if (served === null) return;
        void this.reviewStore.loadUrl();
        this.reviewStore.startUrlRefresh();
      });
    });
    this.destroyRef.onDestroy(() => {
      this.closeApprovalConflict?.();
      this.closeApprovalConflict = null;
      this.approveOpen.set(false);
      this.reopenOpen.set(false);
      this.approval.reset();
    });
    let savedEpisodeId: string | null = null;
    let observedSavedAt: number | null = null;
    effect(() => {
      const episodeId = this.routeState()[0].get('id');
      const savedAt = this.store.savedAt();
      const decisionPending = this.approval.approving() || this.approval.reopening();
      // A mount/entity change establishes the baseline; it is not evidence of a save performed on this page.
      if (episodeId !== savedEpisodeId) {
        savedEpisodeId = episodeId;
        observedSavedAt = savedAt;
        this.savedDetailRefresh = null;
        return;
      }
      if (savedAt === observedSavedAt) return;
      observedSavedAt = savedAt;
      if (savedAt === null || decisionPending) return;
      // Refetch updates detail only, so the savedAt edge cannot be retriggered by its own answer.
      untracked(() => { this.savedDetailRefresh = this.detailStore.refetch(); });
    });
    effect(() => {
      const outcome = this.approval.outcome();
      if (outcome === null) return;
      untracked(() => {
        // The page consumes `outcome` in this effect, before any child (the versions panel host) can read it, so the
        // panel's now-stale needs-reopen notice is cleared here: a successful reopen is the one thing that makes both
        // the notice and the reopen action it offers obsolete (`VersionsStore.prepareRestore`/`clearSelection` own the
        // other two seams). Doing it in an effect would need the approval store to publish the outcome twice.
        if (outcome === 'reopened') this.versions.needsReopen.set(false);
        this.closeApprovalDialogs();
        this.pendingApprovalAnnouncement.set(outcome);
        this.approval.clearOutcome();
      });
    });
    effect(() => {
      const outcome = this.pendingApprovalAnnouncement();
      if (outcome === null || !this.episodesLoaded()) return;
      const text = this.resolveEpisodesKey(outcome === 'approved' ? 'approve.done' : 'reopen.done');
      if (text === null) return;
      untracked(() => {
        this.approvalAnnouncement.set({ text, sequence: ++this.approvalAnnouncementSequence });
        this.snack.open(text, undefined, { duration: 5000, politeness: 'off' });
        this.pendingApprovalAnnouncement.set(null);
      });
    });
    effect(() => {
      if (!this.approval.conflict()) return;
      untracked(() => {
        this.approval.conflict.set(false);
        const intent = this.reopenOpen() ? 'reopen' : 'approve';
        this.approveOpen.set(false);
        this.reopenOpen.set(false);
        // Reuse the editor's conflict resolutions; no cached ETag or second write path.
        this.store.conflictDiffKnown.set(false);
        this.store.conflict.set(true);
        void this.openConflict(intent);
      });
    });
    afterRenderEffect(() => {
      if (!this.restoreApprovalFocus() || this.approveOpen() || this.reopenOpen() || this.conflictDialogOpen()) return;
      this.restoreApprovalFocus.set(false);
      if (this.approvalTrigger?.isConnected && !(this.approvalTrigger instanceof HTMLButtonElement && this.approvalTrigger.disabled)) {
        this.approvalTrigger.focus();
        return;
      }
      const reopen = this.reopenTrigger()?.nativeElement;
      if (reopen !== undefined && !reopen.disabled) { reopen.focus(); return; }
      const heading = this.pageHeader()?.nativeElement.querySelector<HTMLHeadingElement>('h1');
      if (heading !== null && heading !== undefined) { heading.tabIndex = -1; heading.focus(); }
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
    // Cancelling a conflict, or acting on the changed-elsewhere notice, leaves the user in the editor: the save control
    // takes focus back (the dialog restores focus to whatever held it before, which is not necessarily where the draft
    // is saved from). A resolved notice leaves a clean draft, so Save is disabled — and `focus()` on a disabled control
    // does nothing — hence the first editor field as the fallback so focus never falls to the document body.
    afterRenderEffect(() => {
      if (!this.restoreSaveFocus()) return;
      const host = this.editorHost();
      if (host === undefined) return;
      this.restoreSaveFocus.set(false);
      const save = host.nativeElement.querySelector<HTMLButtonElement>('.story-save-bar button');
      if (save !== null && !save.disabled) { save.focus(); return; }
      host.nativeElement.querySelector<HTMLTextAreaElement>('textarea[data-segment]')?.focus();
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
    void this.reloadAfterConfirm().then(this.restoreFocusIfNoticeResolved);
  }

  protected saveMineRemoteNotice(): void {
    void this.store.resolveConflict('saveAsNew').then(this.restoreFocusIfNoticeResolved);
  }

  protected dismissRemoteNotice(): void {
    void this.store.resolveConflict('cancel').then(this.restoreFocusIfNoticeResolved);
  }

  /**
   * Focus returns to the editor only once the notice is actually gone, so a button that is about to be removed cannot
   * take it along. Every action can leave the notice standing: an accepted reload can return early (a save in flight) or
   * the refetch can fail, and a save-as-new second 412 opens the conflict dialog, which owns the focus from there.
   */
  private readonly restoreFocusIfNoticeResolved = (): void => {
    if (this.destroyRef.destroyed || this.store.remoteChange() !== null) return;
    this.restoreSaveFocus.set(true);
  };

  /**
   * The conflict dialog (10.5.10-T1). One dialog at a time, and after every awaited step the page re-checks that it is
   * still alive: a route change during any of them must not open a dialog or touch the store.
   *
   * The loop exists so the dialog can come back carrying the outcome of the user's own last action — a declined
   * confirm returns to it with the draft untouched, a second 412 shows the busy body, a failed reload its retry body.
   * It never re-opens on its own: every iteration is entered only after the user closed the previous dialog.
   */
  private async openConflict(approvalIntent?: 'approve' | 'reopen'): Promise<void> {
    if (this.conflictDialogOpen() || (approvalIntent === undefined && this.store.readOnly()) || this.store.conflict() === null) return;
    const episodeId = this.lastEpisodeId;
    this.conflictDialogOpen.set(true);
    // The retry copy belongs only to a reload the user just attempted here and that failed; every other iteration
    // derives its variant fresh from the store, so a stale error can never pick the dialog's copy.
    let reloadFailed = false;
    try {
      while (!this.destroyRef.destroyed && this.lastEpisodeId === episodeId && (approvalIntent !== undefined || !this.store.readOnly()) && this.store.conflict() !== null) {
        const variant: ConflictVariant = reloadFailed ? 'reloadFailed' : this.store.conflictBusy() ? 'busy' : 'body';
        reloadFailed = false;
        // The diff arrives from an async read: after a read that failed or was fenced nothing is known about the
        // differences, so the detail block is omitted entirely rather than claiming "no differing parts".
        const diffKnown = this.store.conflictDiffKnown() && this.episodesLoaded();
        const dialog = this.conflictDialog.open({
          messageKey: CONFLICT_MESSAGE_KEYS[variant],
          ...(diffKnown ? { detailHeadingKey: 'episodes.conflict.diffHeading', detailTexts: this.diffLabels(this.store.conflictDiff()) } : {}),
          ...(approvalIntent === 'reopen' ? {} : { saveAsNewLabel: 'errors.conflict.saveAsNew' }),
          traceId: this.store.error()?.traceId,
        }, { viewContainerRef: this.viewContainerRef });
        if (approvalIntent !== undefined) this.closeApprovalConflict = () => dialog.close?.('cancel');
        const choice = (await firstValueFrom(dialog.afterClosed())) ?? 'cancel';
        if (this.destroyRef.destroyed || this.lastEpisodeId !== episodeId) return;
        if (choice === 'cancel') {
          await this.store.resolveConflict('cancel');
          if (this.destroyRef.destroyed || this.lastEpisodeId !== episodeId) return;
          if (approvalIntent === undefined) this.restoreSaveFocus.set(true);
          return;
        }
        if (choice === 'reload') {
          const errorBefore = this.store.error();
          // Declining the confirm is not a decision about the conflict: the dialog is the state the user came back to.
          if (!await this.reloadAfterConfirm(approvalIntent !== undefined)) continue;
          if (this.destroyRef.destroyed || this.lastEpisodeId !== episodeId) return;
          if (this.store.conflict() === null) return;
          // Only a reload that came back with a fresh error keeps the conflict standing *and* earned the retry copy.
          reloadFailed = this.store.error() !== null && this.store.error() !== errorBefore;
          continue;
        }
        // Save as new: exactly one attempt. A second 412 is not retried from here — the dialog re-opens in the busy
        // variant and waits for the next explicit click, which is where the user decides what to do with the race.
        await this.store.resolveConflict('saveAsNew');
        if (this.destroyRef.destroyed || this.lastEpisodeId !== episodeId) return;
        if (this.store.conflict() === null) return;
      }
    } finally {
      if (approvalIntent !== undefined) this.closeApprovalConflict = null;
      this.conflictDialogOpen.set(false);
      if (approvalIntent !== undefined && !this.destroyRef.destroyed && this.lastEpisodeId === episodeId) {
        if (approvalIntent === 'approve' && this.approval.status() === 'story_review') this.approveOpen.set(true);
        else if (approvalIntent === 'reopen' && this.approval.canReopen()) this.reopenOpen.set(true);
        else this.restoreApprovalFocus.set(true);
      }
    }
  }

  /** Reload replaces the unsaved text, so it always goes through the destructive confirm — from the dialog and the notice alike. */
  private async reloadAfterConfirm(approvalFlow = false): Promise<boolean> {
    const episodeId = this.lastEpisodeId;
    const confirm = this.confirmDialog.confirm({
      titleKey: 'episodes.conflict.reloadConfirmTitle',
      bodyKey: 'episodes.conflict.reloadConfirmBody',
      confirmLabelKey: 'episodes.conflict.reload',
      destructive: true,
    }, { viewContainerRef: this.viewContainerRef });
    if (approvalFlow) this.closeApprovalConflict = () => confirm.close?.(false);
    const accepted = (await firstValueFrom(confirm.afterClosed())) ?? false;
    if (approvalFlow) this.closeApprovalConflict = null;
    if (this.destroyRef.destroyed || this.lastEpisodeId !== episodeId) return false;
    if (accepted) await this.store.resolveConflict('reload');
    return accepted;
  }

  /**
   * The announcement copy is resolved once, at the moment of the save that produced it, and frozen with its sequence.
   * A key the `episodes` scope cannot resolve (the scope has not loaded, or the language lacks the entry) yields `null`
   * so a raw key is never announced.
   */
  private announcementText(count: number): string | null {
    if (count === 1) return this.resolveEpisodesKey('annotations.announceOne');
    if (count > 0) return this.resolveEpisodesKey('annotations.announce', { count });
    return this.resolveEpisodesKey('annotations.announceNone');
  }

  /** `translate` echoes the scope-prefixed key back when the scope has not loaded; the caller treats that as "no copy". */
  private resolveEpisodesKey(key: string, params: Record<string, unknown> = {}): string | null {
    const text = this.transloco.translate(key, params, 'episodes');
    return text === key || text === `episodes.${key}` ? null : text;
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
