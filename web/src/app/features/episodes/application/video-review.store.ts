import { DestroyRef, Injectable, computed, effect, inject, isDevMode, signal, untracked } from '@angular/core';
import { FormControl, FormGroup, Validators } from '@angular/forms';
import { AppErrorMapper, bindFieldErrors, clearServerErrors } from '@core/errors';
import type { AppError } from '@core/errors';
import { IdempotencyKeys } from '@core/http';
import type { IdempotencyIntent } from '@core/http';
import { SessionStore } from '@core/session';
import { visiblePoll } from '@core/state';
import type { VisiblePoll } from '@core/state';
import { safeFilename } from '@shared/util/safe-filename';
import type { FinalVideoAsset } from '../domain/episode.model';
import type { SkippedScene } from '../domain/render.model';
import { VideoReviewRepository } from '../domain/video-review.repository';
import type { RejectTarget, VideoUrl } from '../domain/video-review.repository';
import { EpisodeDetailStore, isAppError } from './episode-detail.store';

/** WB3/F10: the URL is swapped this long before `expiresAt` — the 15-minute TTL minus a 60 s safety margin. */
const REFRESH_LEAD_MS = 60_000;

/** D17: one due check per visible minute (the check itself is free; a request only when the lead has passed). */
const REFRESH_INTERVAL_MS = 60_000;

/** The reason, and the note, are `1..500` on the wire (09.7.1-T1); the note may also be absent entirely. */
const MAX_TEXT_LENGTH = 500;

/** The note/reason key of the reject command's intent (a dev-console label, never sent; 10.2.3-T1). */
const REJECT_INTENT = 'episode.video.reject';

/** The dialog the page renders under `@if (store.dialog())`; `null` while none is open (the approve-dialog precedent). */
export type VideoDecisionDialog = 'approve' | 'reject';

/** Why a URL fetch ran: the view open, the due check, a manual retry, or the player's own `error` event. */
type UrlFetchSource = 'open' | 'poll' | 'retry' | 'player';

/**
 * True when the server never gave a definitive refusal, so the reject may or may not have been applied: no response
 * at all (`kind network`), a 5xx (`kind server`), or a status this client cannot map — a proxy's 408/425 or a status
 * 0 with no mapped code. The key is KEPT for these, so the user's retry replays one server effect instead of
 * rejecting twice (09.6.1-T1:197). Every mapped refusal (400/401/403/404/409/412/413/422/428/429) is final for the
 * intent. Mirrors `MakeVideoStore`'s predicate (same rule, same parameter list).
 */
function isUnknownOutcome(error: AppError): boolean {
  if (error.kind === 'network' || error.kind === 'server') return true;
  if (error.kind !== 'unknown') return false;
  return error.status === 0 || error.status === 408 || error.status === 425 || error.status >= 500;
}

/**
 * Video review of `/episodes/:id` (10.6.2-T1), provided by the episode page in its component `providers`. It owns the
 * short-lived signed URL (fetch on open, silent swap before expiry, one forced refresh on a player error) and the two
 * decisions, and nothing else: the episode — `nextActions`, the ETag, the final asset, the hint codes — belongs to
 * `EpisodeDetailStore`, the wire (`If-Match`, `Idempotency-Key`, `Retry-After`, the 202's meaning) to
 * `VideoReviewRepository`, the hints' and errors' copy to i18n keys, and the file name to `safe-filename`.
 *
 * No status and no URL is ever painted from a guess: a decision writes nothing locally, the refetch it awaits brings
 * the authoritative state in, and the signed URL lives in one signal and dies with the page (WD10/F10). Every late
 * answer — after teardown, a `:id` change or the end of the session — is discarded by its generation.
 */
@Injectable()
export class VideoReviewStore {
  private readonly repo = inject(VideoReviewRepository);
  private readonly detail = inject(EpisodeDetailStore);
  private readonly keys = inject(IdempotencyKeys);
  private readonly mapper = inject(AppErrorMapper);
  private readonly session = inject(SessionStore);
  private readonly ownerGeneration = this.session.generation();

  private readonly urlState = signal<VideoUrl | null>(null);
  private readonly loadingUrlState = signal(false);
  private readonly urlErrorState = signal<AppError | null>(null);
  private readonly playerErrorState = signal(false);
  private readonly urlFailedState = signal(false);
  private readonly refreshCountState = signal(0);
  private readonly dialogState = signal<VideoDecisionDialog | null>(null);
  private readonly targetState = signal<RejectTarget>('story');
  private readonly decidingState = signal(false);
  private readonly errorState = signal<AppError | null>(null);
  private readonly needsRefreshState = signal(false);
  private readonly acceptedState = signal(false);
  private readonly retryAfterState = signal<number | null>(null);
  private readonly intentState = signal<IdempotencyIntent | null>(null);

  /** The signed URL of the episode, or `null` while there is none. Memory-only: never persisted, never logged. */
  readonly url = this.urlState.asReadonly();
  /** `true` only while the *first* URL of the episode is on its way (the player shows its skeleton then). */
  readonly loadingUrl = this.loadingUrlState.asReadonly();
  /** The mapped failure of the last URL read; a silent refresh that fails sets it and keeps the previous URL. */
  readonly urlError = this.urlErrorState.asReadonly();
  /** The catalogue leaf of `urlError` (`errors.<code>`), or `null`; the wire `detail` is never rendered (D9). */
  readonly urlErrorKey = computed<string | null>(() => {
    const error = this.urlError();
    return error === null ? null : this.mapper.messageKey(error);
  });
  /** A `503 service_unavailable`'s wait, in seconds: the retry stays disabled while `RetryCountdown` runs. */
  readonly urlRetryAfterSec = computed<number | null>(() => this.urlError()?.retryAfterSec ?? null);
  /** A media element reported `error`: the view keeps the player but stops claiming the link is healthy. */
  readonly playerError = this.playerErrorState.asReadonly();
  /**
   * The player is out of options: the URL read that opened the view failed, or the one refresh a player error was
   * allowed failed too. The view replaces the player with `render.review.urlFailed` and a retry button.
   */
  readonly urlFailed = this.urlFailedState.asReadonly();
  /**
   * The count-only hook of a successful URL swap (`data-testid="video-url-refreshed"`, plan §10 O-3): the new URL
   * itself is never observable outside `url`, so a journey can prove a refresh without reading a signed link.
   */
  readonly refreshCount = this.refreshCountState.asReadonly();

  /** The dialog the page renders, or `null`. It closes only on a successful decision (or the user's dismissal). */
  readonly dialog = this.dialogState.asReadonly();
  /** The target of the reject dialog: `story` from "Reject", `re_render` from "Re-make video" (WD4). */
  readonly target = this.targetState.asReadonly();
  /** The optional note of the approve dialog (`≤ 500`); blank is sent as no body at all. */
  readonly note = new FormControl<string>('', { nonNullable: true, validators: [Validators.maxLength(MAX_TEXT_LENGTH)] });
  /** The reject reason (1–500, required on **both** targets — 09.7.1-T1:54), bound by the dialog. */
  readonly reason = new FormControl<string>('', {
    nonNullable: true,
    // An arrow around `Validators.required`: the bare method reference would be an unbound-method lint error, and the
    // dialog's own message for an empty reason is the `required` one the wire's validators name.
    validators: [(control) => Validators.required(control), Validators.maxLength(MAX_TEXT_LENGTH)],
  });
  /** The one decision in flight, and the only thing that releases it is the answer plus the authoritative refetch. */
  readonly deciding = this.decidingState.asReadonly();
  /** The mapped failure of the last decision; `null` while nothing is refused. */
  readonly error = this.errorState.asReadonly();
  /**
   * The catalogue leaf the dialog renders (D9). A 428 `if_match_required` or a 422 `idempotency_key_reused` is this
   * client's own bug, so the code's leaf would describe the wrong thing: the generic copy carries the `traceId`
   * instead (the `MakeVideoStore` rule).
   */
  readonly errorKey = computed<string | null>(() => {
    const error = this.error();
    if (error === null) return null;
    return error.status === 428 || error.status === 422 ? 'errors.unknown_error' : this.mapper.messageKey(error);
  });
  /** `412 episode_conflict`: the retry is offered only once the fresh ETag has landed (10.2.2-T1's ConflictDialog). */
  readonly needsRefresh = this.needsRefreshState.asReadonly();
  /** The wait a `429` asked for, in seconds; it holds the decision buttons back until it is over. */
  readonly retryAfterSec = this.retryAfterState.asReadonly();
  /** The `re_render` 202 was accepted: no render job id is invented and no status is written from it (F12). */
  readonly accepted = this.acceptedState.asReadonly();

  /** The final asset of the reviewed render, metadata only, or `null` (a race, or a `final_video_asset_id` with no row). */
  readonly finalVideo = computed<FinalVideoAsset | null>(() => this.detail.episode()?.finalVideo ?? null);
  /** The render-quality hint **codes** (10 CR-12): the view resolves them to keys, an unknown code renders nothing. */
  readonly notes = computed<readonly string[]>(() => this.detail.episode()?.renderJob?.hints ?? []);
  /** The scenes the image pipeline dropped (03.4.2-T1, soft): `[]` while the field does not exist on the wire. */
  readonly skipped = computed<readonly SkippedScene[]>(() => this.detail.episode()?.renderJob?.images?.skipped ?? []);
  /** The server's own permission for the action (F8): a hint never blocks a decision (C11), a hold or a send does. */
  readonly canApprove = computed(() => (this.detail.episode()?.nextActions ?? []).includes('approve_video') && !this.deciding());
  readonly canReject = computed(() => (this.detail.episode()?.nextActions ?? []).includes('reject_video') && !this.deciding());
  readonly canDownload = computed(() => (this.detail.episode()?.nextActions ?? []).includes('download'));
  /** `safeFilename(title) + ".mp4"` (D4): the server's `Content-Disposition` stays the authority off-origin. */
  readonly downloadName = computed(() => `${safeFilename(this.detail.episode()?.title ?? 'nighttales')}.mp4`);
  /** The instant the due check may swap the URL (`expiresAt − 60 s`), compared with `Date.now()`; `∞` without a URL. */
  readonly refreshDueAt = computed(() => {
    const url = this.url();
    return url === null ? Number.POSITIVE_INFINITY : Date.parse(url.expiresAt) - REFRESH_LEAD_MS;
  });

  /**
   * The due check (D17/WB3). `visiblePoll` runs it at most once a visible minute and only while the tab is visible
   * **and** `ActivityTracker.isActive()`, and `DestroyRef` stops it with the page; the interval itself is no
   * `setInterval` of this store's (D13), and the due comparison is what keeps a request away until the lead passes.
   */
  private readonly poll: VisiblePoll = visiblePoll({ intervalMs: REFRESH_INTERVAL_MS, run: () => this.checkDue() });

  private destroyed = false;
  /** Bumped by every URL fetch, every re-arm and teardown: an answer carrying an older value writes nothing. */
  private urlSeq = 0;
  /** One URL read at a time: a due check that lands while a fetch is in flight issues no second request (WB3). */
  private urlInFlight = false;
  /** The episode the held `url` was minted for: a reused component must never serve the previous episode's link. */
  private urlEpisodeId: string | null = null;
  /** Whether the one refresh a player error is allowed has already been spent for the failure being seen. */
  private playerRefreshSpent = false;

  /** Bumped by every decision, dismissal and teardown: an answer carrying an older value writes nothing. */
  private decisionSeq = 0;
  private intentEpisodeId: string | null = null;
  private rateLimitTimer: number | undefined;

  /** The approve/reject controls as one form, so `bindFieldErrors` resolves the wire pointers (`note`, `reason`). */
  private readonly decisionForm = new FormGroup({ note: this.note, reason: this.reason });

  constructor() {
    inject(DestroyRef).onDestroy(() => {
      this.teardown();
    });
    effect(() => {
      // The session ended (logout, revocation): the signed URL, the held key reference and the dialog go with it —
      // the page routes to `/login` and nothing of the watched cut stays in memory (D6/WD10).
      if (this.session.generation() !== this.ownerGeneration) untracked(() => this.arm(null));
    });
  }

  /**
   * The URL read of the view open. Exactly one request per episode: a second call (a zoneless re-run of the page's
   * init) is a no-op, and a call for another `:id` drops the previous episode's link first. `USER_ACTION` only when
   * the owner pressed a retry — a background read must not extend an idle session (WR11/D7).
   */
  async loadUrl(opts?: { readonly userAction?: boolean }): Promise<void> {
    const episodeId = this.detail.episode()?.id ?? null;
    if (episodeId === null || this.destroyed) return;
    if (this.urlEpisodeId !== episodeId) this.arm(episodeId);
    if (this.url() !== null) return;
    await this.fetchUrl(episodeId, 'open', opts?.userAction === true);
  }

  /** The silent swap (WB3): it keeps the previous URL in place, so playback is never interrupted by a null source. */
  async refreshUrl(): Promise<void> {
    const episodeId = this.urlEpisodeId;
    if (episodeId === null) return;
    await this.fetchUrl(episodeId, 'poll', false);
  }

  /** The owner's own retry after a failed read or a failed player-error refresh: this one carries `USER_ACTION`. */
  async retryUrl(): Promise<void> {
    const episodeId = this.detail.episode()?.id ?? null;
    if (episodeId === null || this.destroyed) return;
    if (this.urlEpisodeId !== episodeId) this.arm(episodeId);
    await this.fetchUrl(episodeId, 'retry', true);
  }

  /** Arms the due check; idempotent, and `DestroyRef` (inside `visiblePoll`) stops it with the store. */
  startUrlRefresh(): void {
    if (this.destroyed) return;
    this.poll.start();
  }

  /**
   * The page left the review branches (10.6.2-T1 slice C): a status this store does not serve owns the view. The URL,
   * its poll, the count and any open decision dialog go with it, so a later return to `video_review`/`ready` for the
   * same episode (the `re_render` → render → `video_review` cycle, a **new** final asset) reads a fresh link instead
   * of serving the previous render's, and no `video-url` request keeps running behind the timeline. Idempotent; the
   * poll restarts on the next `startUrlRefresh()`, and the `DestroyRef` teardown stops it either way.
   */
  release(): void {
    if (this.destroyed) return;
    this.arm(null);
    this.poll.stop();
  }

  /**
   * The media element's `error` event. It sets the flag and forces **one** refresh of the signed URL — an expired link
   * is by far the likeliest cause — and if that refresh fails the view replaces the player with
   * `render.review.urlFailed` + a retry. A further error without a success in between is not another automatic
   * request: it sets the same flag, so the URL endpoint is never hammered by a broken asset.
   */
  onPlayerError(): void {
    if (this.destroyed) return;
    this.playerErrorState.set(true);
    if (this.playerRefreshSpent) {
      this.urlFailedState.set(true);
      return;
    }
    this.playerRefreshSpent = true;
    const episodeId = this.urlEpisodeId ?? this.detail.episode()?.id ?? null;
    if (episodeId === null) return;
    void this.fetchUrl(episodeId, 'player', false);
  }

  /** Opens the approve dialog when the server offers the action, with a fresh (empty) note. */
  openApprove(): void {
    if (!this.canApprove()) return;
    this.resetDecisionMessage();
    this.note.setValue('');
    this.dialogState.set('approve');
  }

  /** Opens the one reject dialog; "Re-make video" opens this same one with `target = 're_render'` (WD4/F12). */
  openReject(target: RejectTarget): void {
    if (!this.canReject()) return;
    this.resetDecisionMessage();
    this.reason.setValue('');
    this.targetState.set(target);
    this.dialogState.set('reject');
  }

  /** The target radio of the reject dialog (the server validates a reason on both targets). */
  setTarget(target: RejectTarget): void {
    this.targetState.set(target);
  }

  /**
   * The user dismissed the dialog: it closes, and the click's unfinished key is dropped with it (never persisted).
   * The dismissal also supersedes any answer still on its way (its refetch and messages are fenced out) and releases
   * the hold, so a dialog that was dismissed mid-flight can neither be reopened by a late answer nor leave the
   * decision buttons disabled forever.
   */
  closeDialog(): void {
    this.decisionSeq++;
    this.dialogState.set(null);
    this.decidingState.set(false);
    this.dropHeldIntent();
    this.resetDecisionMessage();
  }

  /**
   * The approve the dialog just confirmed: one `POST …/video/approve` with the episode's **current** ETag and no key
   * (the route requires none — F9). A blank or whitespace-only note is sent as no body field at all. The refetch is
   * awaited before `deciding` is released, so a second click inside that window cannot approve twice off a stale ETag.
   */
  async approve(): Promise<void> {
    const episode = this.detail.episode();
    if (episode === null || this.destroyed || !this.canApprove() || this.retryAfterState() !== null) return;
    // The previous attempt's message and its bound field errors go first (the `bindFieldErrors` rule: always clear
    // before a submit), so a server refusal on `note` does not block the retry the user is asking for.
    this.resetDecisionMessage();
    if (this.note.invalid) {
      // Longer than the wire's 500: the local `maxlength` is the message, and nothing is sent.
      this.note.markAsTouched();
      return;
    }
    const call = ++this.decisionSeq;
    const note = this.note.value.trim();
    this.decidingState.set(true);
    try {
      await this.repo.approveVideo(episode.id, episode.etag, note === '' ? undefined : note);
      if (this.staleDecision(episode.id, call)) return;
      this.dialogState.set(null);
      // The server owns the new state (the page switches to its `ready` branch off the refetched detail).
      await this.detail.refetch();
    } catch (cause) {
      if (this.staleDecision(episode.id, call)) return;
      await this.routeDecisionFailure(cause);
    } finally {
      if (call === this.decisionSeq && !this.destroyed) this.decidingState.set(false);
    }
  }

  /**
   * The reject the dialog just confirmed: one `POST …/video/reject` with the episode's current ETag, the reason, the
   * target and **one** `IdempotencyIntent` per click — reused by this click's retries while the outcome is unknown,
   * settled on success or any non-retryable answer (09.7.1-T1:167). The `re_render` 202 is `accepted = true` with no
   * render job id invented; either answer refetches, and the refetch is awaited before `deciding` is released.
   */
  async reject(): Promise<void> {
    const episode = this.detail.episode();
    if (episode === null || this.destroyed || !this.canReject() || this.retryAfterState() !== null) return;
    this.resetDecisionMessage();
    const reason = this.reason.value.trim();
    if (reason === '' || this.reason.invalid) {
      // The server requires a reason on both targets (09.7.1-T1:54); marking the control touched is what shows the
      // dialog's own `required`/`maxlength` message without a round-trip.
      this.reason.markAsTouched();
      return;
    }
    const target = this.targetState();
    const call = ++this.decisionSeq;
    const intent = this.intentFor(episode.id);
    this.decidingState.set(true);
    try {
      const answer = await this.repo.rejectVideo(episode.id, episode.etag, reason, target, intent);
      if (this.staleDecision(episode.id, call)) {
        this.settleIntent(intent);
        return;
      }
      this.settleIntent(intent);
      this.acceptedState.set(answer.accepted);
      this.dialogState.set(null);
      await this.detail.refetch();
    } catch (cause) {
      if (this.staleDecision(episode.id, call)) return;
      await this.routeDecisionFailure(cause);
    } finally {
      if (call === this.decisionSeq && !this.destroyed) this.decidingState.set(false);
    }
  }

  /** The due check itself: a no-op until the lead has passed (and while a fetch is in flight, or without a URL). */
  private async checkDue(): Promise<void> {
    if (this.destroyed || Number.isNaN(this.refreshDueAt())) return;
    if (Date.now() < this.refreshDueAt()) return;
    await this.refreshUrl();
  }

  /**
   * One URL read. The previous URL stays in `url` until the answer lands, and then `url` + `expiresAt` move together
   * (WB3). A failure keeps the previous URL and reports the mapped error; when there is nothing to keep (the opening
   * read failed) or the player was already broken, the view is told to render the failed state instead.
   */
  private async fetchUrl(episodeId: string, source: UrlFetchSource, userAction: boolean): Promise<void> {
    if (this.destroyed || this.urlInFlight) return;
    const call = ++this.urlSeq;
    // Whether this read replaces a URL the player is already using: the swap (not the first read) is what the
    // count-only hook of `data-testid="video-url-refreshed"` reports (plan §10 O-3).
    const swapping = this.url() !== null;
    this.urlInFlight = true;
    if (!swapping) this.loadingUrlState.set(true);
    try {
      // The signed URL itself never leaves this method: no log, no `AppError` metadata, no storage (WD10).
      const answer = userAction
        ? await this.repo.getVideoUrl(episodeId, { userAction: true })
        : await this.repo.getVideoUrl(episodeId);
      if (this.staleUrl(episodeId, call)) return;
      this.urlState.set({ url: answer.url, expiresAt: answer.expiresAt });
      this.urlEpisodeId = episodeId;
      this.urlErrorState.set(null);
      this.urlFailedState.set(false);
      this.playerErrorState.set(false);
      this.playerRefreshSpent = false;
      if (swapping) this.refreshCountState.update((count) => count + 1);
    } catch (cause) {
      if (this.staleUrl(episodeId, call)) return;
      this.urlErrorState.set(isAppError(cause) ? cause : this.mapper.from(cause));
      // Nothing to play, or the player was already replaced by a broken link: the view needs the failed state.
      if (this.url() === null || source === 'player') this.urlFailedState.set(true);
    } finally {
      // Only this fetch's own release: a fetch superseded by `arm()` (another episode, an ended session) must not
      // clear the flag of the newer one and let a second GET through.
      if (call === this.urlSeq) {
        this.urlInFlight = false;
        this.loadingUrlState.set(false);
      }
    }
  }

  /**
   * One decision failure → the signals the dialog needs (D9), by code, never by copy. A non-definitive answer keeps
   * the held key, so a retry replays one server effect; every mapped refusal settles it.
   */
  private async routeDecisionFailure(cause: unknown): Promise<void> {
    const error = isAppError(cause) ? cause : this.mapper.from(cause);
    if (isUnknownOutcome(error)) {
      this.errorState.set(error);
      if (isDevMode()) console.warn('video decision had no answer', error.code, error.traceId ?? '-');
      return;
    }
    this.dropHeldIntent();
    this.errorState.set(error);
    if (error.kind === 'validation') {
      // `400 validation_failed`: the wire pointers are bound onto the controls they name (the message is the code's
      // own key, never the wire text) and the dialog stays open on the field the server refused.
      bindFieldErrors(this.decisionForm, error, { markTouched: true });
      return;
    }
    if (error.status === 412) {
      // The ETag this click carried is stale: the retry is offered only on the fresh one, so the read is awaited and
      // `needsRefresh` holds the confirm back until it has landed.
      this.needsRefreshState.set(true);
      await this.detail.refetch();
      this.needsRefreshState.set(false);
      return;
    }
    if (error.status === 409) {
      // The episode moved on (another tab decided first): re-render from the fresh `nextActions` instead of retrying.
      void this.detail.refetch();
      return;
    }
    if (error.status === 429) {
      this.armRateLimit(error.retryAfterSec);
      return;
    }
    if (error.status === 428 || error.status === 422) {
      // Our own bug (a missing `If-Match`, a reused key): `errorKey` renders the generic copy, and `traceId` is what
      // support needs. The server's `detail` is never rendered.
      if (isDevMode()) console.warn('video decision sent an unusable request', error.code, error.traceId ?? '-');
    }
  }

  /** Holds the decisions back for the seconds the server asked for, then releases them (the countdown is the view's). */
  private armRateLimit(seconds: number | undefined): void {
    this.clearRateLimit();
    if (seconds === undefined || !Number.isSafeInteger(seconds) || seconds <= 0) return;
    this.retryAfterState.set(seconds);
    this.rateLimitTimer = window.setTimeout(() => {
      this.rateLimitTimer = undefined;
      this.retryAfterState.set(null);
      // The wait was the whole message: once it expires the 429 the user was looking at is done with.
      if (this.errorState()?.status === 429) this.errorState.set(null);
    }, seconds * 1_000);
  }

  private clearRateLimit(): void {
    if (this.rateLimitTimer !== undefined) {
      window.clearTimeout(this.rateLimitTimer);
      this.rateLimitTimer = undefined;
    }
    this.retryAfterState.set(null);
  }

  /**
   * Clears the previous refusal, its wait and the server's field errors: a new attempt starts from a clean dialog.
   * The generation is the caller's business — a new click, a dismissal and a teardown bump `decisionSeq` themselves,
   * so a fence taken here would invalidate the very call the caller is about to make.
   */
  private resetDecisionMessage(): void {
    this.errorState.set(null);
    this.needsRefreshState.set(false);
    this.acceptedState.set(false);
    this.clearRateLimit();
    clearServerErrors(this.decisionForm);
  }

  /**
   * The held, unfinished intent when it belongs to **this** episode (a retry of the same click, e.g. after a lost
   * response), otherwise a new key. An intent left over from another `:id` — the page reuses the route — is settled
   * and replaced: the API refuses a key reused on another episode with 422 `idempotency_key_reused` (09.6.1-T1:217).
   */
  private intentFor(episodeId: string): IdempotencyIntent {
    const held = this.intentState();
    if (held !== null && !held.isSettled && this.intentEpisodeId === episodeId) return held;
    if (held !== null) this.settleIntent(held);
    const intent = this.keys.begin(REJECT_INTENT);
    this.intentState.set(intent);
    this.intentEpisodeId = episodeId;
    return intent;
  }

  /** Settles and drops `intent`, but only while it is still the held one: a newer click's key is never touched. */
  private settleIntent(intent: IdempotencyIntent): void {
    if (this.intentState() !== intent) return;
    intent.settle();
    this.intentState.set(null);
    this.intentEpisodeId = null;
  }

  private dropHeldIntent(): void {
    const held = this.intentState();
    if (held !== null) held.settle();
    this.intentState.set(null);
    this.intentEpisodeId = null;
  }

  /** Forgets everything that belonged to the previous episode (or to the ended session) without any request. */
  private arm(episodeId: string | null): void {
    this.urlSeq++;
    this.urlInFlight = false;
    this.playerRefreshSpent = false;
    this.urlState.set(null);
    this.urlEpisodeId = episodeId;
    this.loadingUrlState.set(false);
    this.urlErrorState.set(null);
    this.urlFailedState.set(false);
    this.playerErrorState.set(false);
    this.refreshCountState.set(0);
    this.dialogState.set(null);
    this.dropHeldIntent();
    this.resetDecisionMessage();
    this.decisionSeq++;
    this.decidingState.set(false);
  }

  /** True when the answer belongs to a superseded fetch, another episode or a torn-down/ended session. */
  private staleUrl(episodeId: string, call: number): boolean {
    return (
      this.destroyed ||
      call !== this.urlSeq ||
      (this.detail.episode()?.id ?? null) !== episodeId ||
      this.session.generation() !== this.ownerGeneration
    );
  }

  /** True when the answer belongs to a superseded decision, another episode or a torn-down/ended session. */
  private staleDecision(episodeId: string, call: number): boolean {
    return this.destroyed || call !== this.decisionSeq || this.session.generation() !== this.ownerGeneration
      || (this.detail.episode()?.id ?? null) !== episodeId;
  }

  /** The page is gone: the URL, the held key reference, the wait and the dialog die with it (nothing is stored). */
  private teardown(): void {
    this.destroyed = true;
    this.urlSeq++;
    this.decisionSeq++;
    this.urlState.set(null);
    this.urlEpisodeId = null;
    this.urlInFlight = false;
    this.dialogState.set(null);
    this.intentState.set(null);
    this.intentEpisodeId = null;
    this.clearRateLimit();
  }
}
