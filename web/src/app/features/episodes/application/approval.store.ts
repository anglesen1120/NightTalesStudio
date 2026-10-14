import { DestroyRef, Injectable, computed, effect, inject, isDevMode, signal } from '@angular/core';
import type { Signal } from '@angular/core';
import { AppErrorMapper } from '@core/errors';
import type { AppError } from '@core/errors';
import { SessionStore } from '@core/session';
import type { Attestation, AttestationAcceptance, StorySnapshot } from '../domain/story.model';
import { StoryRepository } from '../domain/story.repository';
import { EpisodeDetailStore, isAppError } from './episode-detail.store';
import { StoryEditorStore } from './story-editor.store';

/** What a completed approve or reopen announced: the page reads it once to close the dialog and announce politely. */
export type ApprovalOutcomeKind = 'approved' | 'reopened';

/**
 * The 409 codes an approve can be refused with, other than the state conflict (`episode_invalid_state` is also here —
 * it is the reason region plus the page's `nextActions`). Their mapped `errors.<code>` copy is the dialog's `blocked`
 * region; the flags themselves are never held here (WR6: `StoryEditorStore` owns `keywordFlags`).
 */
const BLOCKED_CODES: readonly string[] = [
  'episode_invalid_state', 'story_fact_sheet_outdated', 'story_sources_required', 'story_too_similar_to_source', 'story_moderated', 'keywords_flagged',
];

/**
 * Page-scoped orchestration of approve / reopen (README D13: signals only, never provided in root). It owns the
 * command state and the refusal routing; the dialogs are presentational and the editor store keeps the draft, the
 * ETag and the keyword flags. Every request re-reads the ETag at call time and is fenced by the session generation
 * and the episode on screen, so an answer that outlived its episode writes nothing (a stale tab cannot approve).
 */
@Injectable()
export class ApprovalStore {
  private readonly detail = inject(EpisodeDetailStore);
  private readonly editor = inject(StoryEditorStore);
  private readonly repo = inject(StoryRepository);
  private readonly mapper = inject(AppErrorMapper);
  private readonly session = inject(SessionStore);

  /**
   * The status and story ETag the server answered with. They win over the (async) detail while the read a successful
   * command started is out, and they stay after a read that failed for real: the server did answer, and the detail on
   * screen is the older truth until a read lands. Both are dropped by a successful read, `reset()` and an episode change.
   */
  private readonly recordedStatus = signal<string | null>(null);
  private readonly recordedEtag = signal<string | null>(null);
  /** The episode the record belongs to: another episode's story never inherits it. */
  private recordedForEpisode: string | null = null;
  /** Reads this store started that have not answered yet; kept above zero when a newer read superseded ours (P3). */
  private readonly pendingReads = signal(0);
  /** The text version the user ticked; the tick dies with it, so a moved block can never inherit it. */
  private readonly acceptedTextVersion = signal<string | null>(null);
  /** A `409 story_moderated` verdict, remembered against the draft revision it was about. */
  private readonly moderatedState = signal<{ readonly episodeId: string; readonly revision: number } | null>(null);
  /** The story on screen, as the detail store sees it; every signal below derives from this one read. */
  private readonly currentStory: Signal<StorySnapshot | null> = computed(() => this.detail.episode()?.currentStory ?? null);

  /** The server-owned block of the current story; `null` for a plain episode or when no story is loaded. */
  readonly attestation: Signal<Attestation | null> = computed(() => this.currentStory()?.attestation ?? null);
  /**
   * Whether the user ticked the block that is on screen *now*. The tick is remembered as the text version it was given
   * for, never as a bare boolean: a refetch that moves the text version (or a block that disappears) takes it back.
   */
  readonly attestationAccepted: Signal<boolean> = computed(() => {
    const current = this.attestation();
    return current !== null && this.acceptedTextVersion() === current.textVersion;
  });
  readonly approving = signal(false);
  readonly reopening = signal(false);
  /** A failure the user can only wait out or report (429/428/anything unmapped); the text gate codes are `blocked`. */
  readonly error = signal<AppError | null>(null);
  /** The refusal the approve dialog renders: the 409 gate codes and `keywords_flagged`. */
  readonly blocked = signal<AppError | null>(null);
  /** `400 story_attestation_required`: the box the user must tick again after the server's text version moved. */
  readonly checkboxInvalid = signal(false);
  /** The wait the server asked for (integer seconds) while a 429 holds approve back; `null` when nothing is pending. */
  readonly retryAfterSec = signal<number | null>(null);
  /** `true` when a 412 needs the ConflictDialog (the page owns the dialog and the reload). */
  readonly conflict = signal(false);
  /** Advisory only (C11): hints are shown next to the dialog and never disable it. */
  readonly hintCount: Signal<number> = computed(() => this.currentStory()?.qualityHints.length ?? 0);
  /** A completed command, read once by the page to close the dialog and announce; `null` before the first one. */
  readonly outcome = signal<ApprovalOutcomeKind | null>(null);
  /** The story status on screen: the server's own answer until a detail read lands. */
  readonly status: Signal<string | null> = computed(() => this.recordedStatus() ?? this.detail.episode()?.status ?? null);

  /**
   * True while the detail read a successful command started is still out. While it is, the story on screen is not the
   * one the server just answered about — and both commands If-Match an ETag read off that story — so both buttons are
   * held back. A read that a newer one superseded keeps this true until *that* read lands: it owns the answer, and
   * `loading` is exactly that fact.
   */
  private readonly awaitingRefetch: Signal<boolean> = computed(() => this.pendingReads() > 0 && this.detail.loading());

  /**
   * The four WR6 disabling conditions of the Approve control *without* the tick: the header button opens the dialog on
   * this and the tick is asked for inside it. `qualityHints` never disable it, and the fact-sheet 409 codes are copy,
   * not a lock. A success whose own detail read is still in flight disables it too: the story on screen is not the one
   * the server just answered about.
   */
  readonly canOpenApprove: Signal<boolean> = computed(() => {
    if (this.awaitingRefetch()) return false;
    if (this.status() !== 'story_review' || this.approving() || this.reopening()) return false;
    const story = this.currentStory();
    if (story === null) return false;
    if (this.editor.dirty() || this.editor.saving()) return false;
    if (this.moderationBlocked()) return false;
    if (this.editor.videoBlocked()) return false;
    return this.retryAfterSec() === null;
  });

  /**
   * The dialog's confirm: `canOpenApprove` plus the true-crime tick when the block on screen requires one. A required
   * block with no live tick is the only condition this adds, so the two signals can never disagree on anything else.
   */
  readonly canApprove: Signal<boolean> = computed(() => {
    if (!this.canOpenApprove()) return false;
    const story = this.currentStory();
    return story === null ? false : !(story.attestation?.required ?? false) || this.attestationAccepted();
  });

  /**
   * An approved story can be put back into review; the detail (and with it the editor's `readOnly`) refetches. The
   * server wait guards both commands, so a 429 on either one holds this button back exactly like it holds approve, and
   * the read a successful command started must have landed first: a reopen sent mid-flight carries a pre-approve ETag
   * and is refused as a conflict.
   */
  readonly canReopen: Signal<boolean> = computed(() =>
    !this.awaitingRefetch() && this.status() === 'story_approved' && this.currentStory() !== null &&
    !this.approving() && !this.reopening() && this.retryAfterSec() === null);

  /** True while a `409 story_moderated` verdict still describes the draft on screen (nothing changed it since). */
  private readonly moderationBlocked: Signal<boolean> = computed(() => {
    const mark = this.moderatedState();
    if (mark === null) return false;
    return mark.episodeId === (this.detail.episode()?.id ?? null) && mark.revision === this.editor.version();
  });

  private readonly ownerGeneration = this.session.generation();
  private destroyed = false;
  /** Bumped by every command, destroy and session change: an answer carrying an older value is discarded. */
  private callSeq = 0;
  private rateLimitTimer: number | undefined;

  constructor() {
    inject(DestroyRef).onDestroy(() => { this.destroyed = true; this.callSeq++; this.clearRateLimit(); });
    /**
     * The two ways a record ends outside a command: a read that superseded a command's own read settles here (it owns
     * the answer, so the record is spent and the hold is released with it), and a record never outlives its episode.
     */
    effect(() => {
      if (this.pendingReads() > 0 && !this.detail.loading()) {
        if (this.detail.error() === null) this.clearRecord();
        this.pendingReads.set(0);
      }
      const episodeId = this.detail.episode()?.id ?? null;
      if (this.recordedForEpisode !== null && episodeId !== this.recordedForEpisode) this.clearRecord();
    });
    // A new session owns no command state (README D6/SESSION_SCOPED): a late answer must not write into it either.
    effect(() => {
      if (this.session.generation() !== this.ownerGeneration) this.reset();
    });
  }

  /**
   * Approves the story version on screen. The ETag is read from the detail at call time (never a copy captured when
   * the dialog opened), the tick is sent only when the current story really requires it, and the status is written
   * only from the server's answer: there is no optimistic state.
   */
  async approve(episodeId: string): Promise<boolean> {
    if (!this.canApprove()) return false;
    const story = this.currentStory();
    if (story === null) return false;
    let acceptance: AttestationAcceptance | undefined;
    if (story.attestation?.required === true) {
      // A required block with no live tick (or no text to have read) is never attested to on the user's behalf.
      if (!this.attestationAccepted() || story.attestation.text === null) return false;
      acceptance = { accepted: true, textVersion: story.attestation.textVersion };
    }
    const call = ++this.callSeq;
    this.approving.set(true); this.error.set(null); this.blocked.set(null); this.clearRateLimit();
    try {
      const answer = await this.repo.approve(episodeId, story.etag, acceptance);
      if (this.stale(episodeId, call)) return false;
      this.record(episodeId, answer.status, answer.etag, call);
      this.moderatedState.set(null);
      // The tick was given for the version that was just approved: it is spent, not re-derived from the answer.
      this.acceptedTextVersion.set(null);
      this.checkboxInvalid.set(false);
      this.outcome.set('approved');
      return true;
    } catch (cause) {
      if (this.stale(episodeId, call)) return false;
      this.route(episodeId, cause, false);
      return false;
    } finally {
      if (call === this.callSeq && this.active()) this.approving.set(false);
    }
  }

  /** Reopens an approved story for editing: the server answers the new status, the detail then refetches. */
  async reopen(episodeId: string): Promise<boolean> {
    if (!this.canReopen()) return false;
    const story = this.currentStory();
    if (story === null) return false;
    const call = ++this.callSeq;
    this.reopening.set(true); this.error.set(null); this.blocked.set(null); this.clearRateLimit();
    try {
      // The record is the freshest ETag the server has answered with (a failed read leaves the story on screen stale),
      // so it is preferred over the one the detail still carries.
      const answer = await this.repo.reopen(episodeId, this.recordedEtag() ?? story.etag);
      if (this.stale(episodeId, call)) return false;
      this.record(episodeId, answer.status, answer.etag, call);
      // The server cleared the attestation of the approved version: a tick given before a reopen is not carried over.
      this.acceptedTextVersion.set(null);
      this.checkboxInvalid.set(false);
      this.outcome.set('reopened');
      return true;
    } catch (cause) {
      if (this.stale(episodeId, call)) return false;
      this.route(episodeId, cause, true);
      return false;
    } finally {
      if (call === this.callSeq && this.active()) this.reopening.set(false);
    }
  }

  /**
   * The user's tick. It is recorded as the text version it was given for: a refetch that moves the version takes the
   * tick back on its own, so a stale tick is never sent. Unticking (or ticking with no block on screen) records
   * nothing, and taking a tick back never clears the invalid mark — `clearBlocked` and a fresh tick own that.
   */
  setAccepted(accepted: boolean): void {
    if (!accepted) { this.acceptedTextVersion.set(null); return; }
    const current = this.attestation();
    if (current === null) return;
    this.acceptedTextVersion.set(current.textVersion);
    this.checkboxInvalid.set(false);
  }

  /** Dismisses the current refusal (the dialog closed or the user asked to try again), including a spent wait. */
  clearBlocked(): void {
    this.blocked.set(null); this.error.set(null); this.clearRateLimit();
  }

  /** The page consumed the announcement: the next command announces itself again. */
  clearOutcome(): void {
    this.outcome.set(null);
  }

  /** SESSION_SCOPED (D6): the page calls it on a session end, and the generation effect calls it on a new session. */
  reset(): void {
    this.callSeq++;
    this.approving.set(false); this.reopening.set(false);
    this.error.set(null); this.blocked.set(null); this.checkboxInvalid.set(false);
    this.conflict.set(false); this.acceptedTextVersion.set(null);
    this.clearRecord(); this.pendingReads.set(0);
    this.moderatedState.set(null); this.outcome.set(null);
    this.clearRateLimit();
  }

  /**
   * Remembers one command's answer and starts the read that confirms it against the detail. The ETag is remembered
   * because it is the freshest one the server has answered with: a read that never lands leaves the detail holding the
   * pre-command story, and the next command must still send the ETag that matches what the server just did.
   */
  private record(episodeId: string, status: string, etag: string, call: number): void {
    this.recordedForEpisode = episodeId;
    this.recordedStatus.set(status);
    this.recordedEtag.set(etag);
    this.pendingReads.update((count) => count + 1);
    void this.refreshDetail(call);
  }

  private clearRecord(): void {
    this.recordedForEpisode = null;
    this.recordedStatus.set(null);
    this.recordedEtag.set(null);
  }

  /**
   * The read that confirms a command's answer against the detail. `EpisodeDetailStore.refetch` answers `null` both for a
   * read that failed and for one a newer read superseded, and the two need opposite things: a real failure (the detail
   * error is set) keeps the server's answer — once a snapshot exists the page shows no detail error region, so dropping
   * the record would put the stale story, and its stale ETag, back on screen — while a superseded read leaves the answer
   * to the newer read and keeps the hold, which the constructor effect releases when that read lands.
   */
  private async refreshDetail(call: number): Promise<void> {
    const fresh = await this.detail.refetch();
    if (!this.active()) return;
    // Superseded, not failed: the newer read owns both the answer and the hold, so nothing is written here (P3).
    if (fresh === null && this.detail.error() === null) return;
    // A newer command owns the truth now: its record and its own read stand (the constructor effect releases the hold).
    if (call !== this.callSeq) return;
    this.pendingReads.set(0);
    if (fresh !== null) this.clearRecord();
  }

  /**
   * One failure → the signal the screen needs (D9). The routing is by code, never by copy: a 412 is the ConflictDialog
   * and leaves the status alone, the 409 gate codes are the dialog's `blocked` region, `keywords_flagged` is handed to
   * the editor store once (WR6, fenced by the episode the request was sent for), and everything else is `error`.
   */
  private route(episodeId: string, cause: unknown, reopen: boolean): void {
    const error = isAppError(cause) ? cause : this.mapper.from(cause);
    if (error.status === 412) { this.conflict.set(true); return; }
    if (error.status === 429) { this.error.set(error); this.armRateLimit(error.retryAfterSec); return; }
    if (reopen) {
      // A reopen refused because the episode already moved (another tab): the fresh state is the answer, not a lock.
      this.error.set(error);
      if (error.status === 409) void this.detail.refetch();
      return;
    }
    if (error.status === 400 && error.code === 'story_attestation_required') {
      // The text version moved under the user: refetch once so the next confirm sends the server's current one.
      this.error.set(error); this.checkboxInvalid.set(true); this.acceptedTextVersion.set(null);
      void this.detail.refetch();
      return;
    }
    if (error.status === 409 && BLOCKED_CODES.includes(error.code)) {
      this.blocked.set(error);
      // The rows are the feedback when at least one of them is usable; with none, the mapped copy has to carry the
      // refusal itself, or the dialog would open with nothing to say.
      if (error.code === 'keywords_flagged' && !this.editor.applyKeywordsFlaggedProblem(error, episodeId)) this.error.set(error);
      // The moderation verdict describes the draft that was sent: it holds until an edit changes that draft.
      if (error.code === 'story_moderated') this.moderatedState.set({ episodeId, revision: this.editor.version() });
      // `episode_invalid_state` is also the reason the nextActions region has to be re-read.
      if (error.code === 'episode_invalid_state') void this.detail.refetch();
      return;
    }
    if (error.status === 428 && isDevMode()) console.warn('if_match_required', error.traceId ?? '-');
    this.error.set(error);
  }

  /**
   * Holds approve back for the seconds the server asked for, then releases it. The dialog's `RetryCountdown` is the
   * visible clock and cannot report its own end, so the store owns this deadline; both are cleared with the store.
   */
  private armRateLimit(seconds: number | undefined): void {
    this.clearRateLimit();
    if (seconds === undefined || !Number.isSafeInteger(seconds) || seconds <= 0) return;
    this.retryAfterSec.set(seconds);
    this.rateLimitTimer = window.setTimeout(() => {
      this.rateLimitTimer = undefined;
      this.retryAfterSec.set(null);
      // The wait is the whole message: once it expires the 429 the user was looking at is done with.
      if (this.error()?.status === 429) this.error.set(null);
    }, seconds * 1_000);
  }

  private clearRateLimit(): void {
    if (this.rateLimitTimer !== undefined) { window.clearTimeout(this.rateLimitTimer); this.rateLimitTimer = undefined; }
    this.retryAfterSec.set(null);
  }

  private active(): boolean { return !this.destroyed && this.session.generation() === this.ownerGeneration; }

  /** True when the answer belongs to a superseded command, session or episode: it must not write anything. */
  private stale(episodeId: string, call: number): boolean {
    return !this.active() || call !== this.callSeq || (this.detail.episode()?.id ?? null) !== episodeId;
  }
}
