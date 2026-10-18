import { DestroyRef, Injectable, computed, inject, isDevMode, signal } from '@angular/core';
import { MatDialogRef } from '@angular/material/dialog';
import { firstValueFrom } from 'rxjs';
import { AppErrorMapper } from '@core/errors';
import type { AppError } from '@core/errors';
import { IdempotencyKeys } from '@core/http';
import type { IdempotencyIntent } from '@core/http';
import { ConfirmDialog } from '@shared/ui';
import type { ConfirmDialogComponent } from '@shared/ui/confirm-dialog/confirm-dialog';
import { RenderRepository } from '../domain/render.repository';
import { EpisodeDetailStore, isAppError } from './episode-detail.store';
import { StoryEditorStore } from './story-editor.store';

/** The server's own permission for the action (F8): neither present means the button is absent, not disabled. */
const RENDER_ACTION = 'render';
const RETRY_RENDER_ACTION = 'retry_render';

/**
 * True when the server never gave a definitive refusal, so the command may or may not have been applied: no response
 * at all (`kind network`), a 5xx (`kind server`), or a status this client cannot map — a proxy's 408/425 or a status 0
 * with no mapped code. The key is KEPT for these, so the user's retry replays one server effect instead of reserving
 * credit twice (09.6.1-T1:197). Every mapped refusal (400/401/403/404/409/412/413/422/428/429) is final for the intent.
 */
function isUnknownOutcome(error: AppError): boolean {
  if (error.kind === 'network' || error.kind === 'server') return true;
  if (error.kind !== 'unknown') return false;
  return error.status === 0 || error.status === 408 || error.status === 425 || error.status >= 500;
}

const ACTION_KEY = 'episodes.render.make.action' as const;
const RETRY_KEY = 'episodes.render.make.retry' as const;
const BLOCKED_KEY = 'episodes.render.make.blockedKeywords' as const;

/** The lifecycle labels the button renders; `null` from the store means the button is absent (F8). */
export type MakeVideoLabelKey = typeof ACTION_KEY | typeof RETRY_KEY;

/** The reason a *present* button is disabled (D16: visible text, never a tooltip-only explanation). */
export type MakeVideoReasonKey = typeof BLOCKED_KEY;

/**
 * Page-scoped orchestration of the "Make video" action (10.6.1-T2), provided by the episode page in its component
 * `providers`. It owns the command state only: the episode (and with it `nextActions` and the ETag) belongs to
 * `EpisodeDetailStore`, `keywordFlags`/`videoBlocked` to `StoryEditorStore` (WR6 — no second flag store here), and
 * the wire — `If-Match`, `Idempotency-Key`, `Location`, `Retry-After` — to `RenderRepository`.
 *
 * The command is one click → one confirm → one POST. No status is ever written locally: on a 202 the timeline takes
 * over as soon as the server reports the job, which the event-driven refetch of the episode detail already brings in
 * (F1). The held `IdempotencyIntent` is reused by every retry of that click and settled on success or any
 * non-retryable answer, so an unknown outcome replays one server effect while a genuinely new click mints a new key
 * (09.6.1-T1:197).
 */
@Injectable()
export class MakeVideoStore {
  private readonly repo = inject(RenderRepository);
  private readonly detail = inject(EpisodeDetailStore);
  private readonly editor = inject(StoryEditorStore);
  private readonly dialogs = inject(ConfirmDialog);
  private readonly keys = inject(IdempotencyKeys);
  private readonly mapper = inject(AppErrorMapper);

  private readonly submittingState = signal(false);
  private readonly errorState = signal<AppError | null>(null);
  private readonly needsRefreshState = signal(false);
  private readonly retryAfterState = signal<number | null>(null);

  readonly submitting = this.submittingState.asReadonly();
  /** The mapped failure the page shows as `errors.<code>`; `null` while nothing is refused. */
  readonly error = this.errorState.asReadonly();

  /**
   * The catalogue leaf the view renders (D9), or the generic copy for our own client-bug answers: a 428
   * `if_match_required` or a 422 `idempotency_key_reused` is this client's mistake, so the code's own leaf would
   * describe the wrong thing, and the `unknown_error` leaf carries `traceId` instead (plan U6, the 10.5.2-T1 rule).
   * `null` while nothing is refused.
   */
  readonly errorKey = computed<string | null>(() => {
    const error = this.error();
    if (error === null) return null;
    return error.status === 428 || error.status === 422 ? 'errors.unknown_error' : this.mapper.messageKey(error);
  });
  /** `412 episode_conflict`, after the refetch: the view asks the user to try again on the new ETag. */
  readonly needsRefresh = this.needsRefreshState.asReadonly();
  /** The wait a `429 spend_cap_reached` asked for, in seconds; it feeds `RetryCountdown` and holds the button back. */
  readonly retryAfterSec = this.retryAfterState.asReadonly();
  private readonly intentState = signal<IdempotencyIntent | null>(null);
  /** The episode the held intent was minted for: a page that moves to another `:id` must never replay its key. */
  private intentEpisodeId: string | null = null;

  /** The one intent of the click in progress, reused by every retry until a terminal answer settles it. */
  readonly intent = this.intentState.asReadonly();

  /** WR6: the flagged segments are the editor store's; this store only reads the gate they arm. */
  readonly blockedByKeywords = computed(() => this.editor.videoBlocked());

  /** Whether the server currently offers the action at all (`nextActions`), whatever the local gates say. */
  readonly allowed = computed(() => {
    const actions = this.detail.episode()?.nextActions ?? [];
    return actions.includes(RENDER_ACTION) || actions.includes(RETRY_RENDER_ACTION);
  });

  /**
   * The action as the button sees it: offered by the server, nothing flagged, no submission in flight and no wait the
   * server asked for. A `429` holds it back exactly like the approve action (10.5.6-T1's `canOpenApprove`).
   */
  readonly canMakeVideo = computed(() => this.allowed() && !this.blockedByKeywords() && !this.submitting() && this.retryAfterSec() === null);

  /**
   * The lifecycle action key, or `null` when the button is absent. `retry` only when `nextActions` offers the retry
   * and not a fresh render, so a re-make after a cancelled job reads as the plain action.
   */
  readonly labelKey = computed<MakeVideoLabelKey | null>(() => {
    const actions = this.detail.episode()?.nextActions ?? [];
    if (actions.includes(RENDER_ACTION)) return ACTION_KEY;
    return actions.includes(RETRY_RENDER_ACTION) ? RETRY_KEY : null;
  });

  /** Why a *present* button is disabled: the flagged keywords, never a reason for an action the server does not offer. */
  readonly reasonKey = computed<MakeVideoReasonKey | null>(() => (this.allowed() && this.blockedByKeywords() ? BLOCKED_KEY : null));

  private destroyed = false;
  /** Bumped by every command, teardown and dialog: an answer carrying an older value writes nothing. */
  private callSeq = 0;
  /** The confirm dialog of the click in progress: a second click while it is open is a no-op. */
  private confirmRef: MatDialogRef<ConfirmDialogComponent, boolean> | null = null;
  private rateLimitTimer: number | undefined;

  constructor() {
    inject(DestroyRef).onDestroy(() => {
      this.destroyed = true;
      this.clearRateLimit();
      this.dismiss();
    });
  }

  /**
   * The click: one confirm, then one `POST /episodes/{id}/render` with the episode's **current** ETag (read at call
   * time, never when the dialog opened) and the click's own key. Returning early when the action is not offered is
   * what makes a double click one request; the dialog reference does the same while it is still open.
   */
  async makeVideo(): Promise<void> {
    if (!this.canMakeVideo() || this.confirmRef !== null) return;
    const episode = this.detail.episode();
    if (episode === null) return;
    if (!(await this.confirm())) return;
    // The button state may have moved while the dialog was open (a refetch from another tab): the answer is the
    // current one, and nothing is sent for a command the server no longer offers.
    if (!this.canMakeVideo()) return;
    const call = ++this.callSeq;
    this.submittingState.set(true);
    this.errorState.set(null);
    this.needsRefreshState.set(false);
    this.clearRateLimit();
    const intent = this.intentFor(episode.id);
    try {
      await this.repo.requestRender(episode.id, episode.etag, intent);
      if (this.stale(episode.id, call)) {
        // The page moved on while this was in flight: the answer belongs to another `:id`, so no key of this episode
        // is left behind for the next click to replay on a different one (D1).
        this.dropIntent(intent);
        return;
      }
      // 202: the key is spent and the server owns the new state. Nothing is written here — the episode's own status
      // and the timeline branch take over once the detail reports the job.
      this.dropIntent(intent);
      void this.detail.refetch();
    } catch (cause) {
      if (this.stale(episode.id, call)) {
        this.dropIntent(intent);
        return;
      }
      await this.route(episode.id, intent, cause);
    } finally {
      if (call === this.callSeq && !this.destroyed) this.submittingState.set(false);
    }
  }

  /**
   * The view is gone (or the page re-initialises): the open confirm is closed, the held intent reference is dropped
   * (its key is never persisted anywhere), the in-flight hold is released and late answers are fenced. The server keeps
   * whatever it was told; a reopened page re-derives everything from the detail.
   */
  dismiss(): void {
    this.callSeq++;
    this.confirmRef?.close(false);
    this.confirmRef = null;
    this.intentState.set(null);
    this.intentEpisodeId = null;
    this.submittingState.set(false);
  }

  /** One `ConfirmDialog` per click, with the empty cost slot D14/F19 asks for. */
  private async confirm(): Promise<boolean> {
    const ref = this.dialogs.confirm({
      titleKey: 'episodes.render.make.confirm.title',
      bodyKey: 'episodes.render.make.confirm.body',
      confirmLabelKey: ACTION_KEY,
    });
    this.confirmRef = ref;
    const accepted = (await firstValueFrom(ref.afterClosed())) ?? false;
    this.confirmRef = null;
    return accepted;
  }

  /**
   * The held, unfinished intent when it belongs to **this** episode (a retry of the same click, e.g. after a lost
   * response), otherwise a new key. An intent left over from another `:id` — the page reuses the route — is settled
   * and replaced: the API refuses a key reused on another episode with 422 `idempotency_key_reused` (09.6.1-T1:217).
   */
  private intentFor(episodeId: string): IdempotencyIntent {
    const held = this.intent();
    if (held !== null && !held.isSettled && this.intentEpisodeId === episodeId) return held;
    if (held !== null) this.dropIntent(held);
    const intent = this.keys.begin('episode.render');
    this.intentState.set(intent);
    this.intentEpisodeId = episodeId;
    return intent;
  }

  /** Settles and drops `intent`, but only while it is still the held one: a newer click's key is never touched. */
  private dropIntent(intent: IdempotencyIntent): void {
    if (this.intent() !== intent) return;
    intent.settle();
    this.intentState.set(null);
    this.intentEpisodeId = null;
  }

  /**
   * One failure → the signal the screen needs (D9), by code, never by copy. A non-definitive answer (`isUnknownOutcome`:
   * no response, a 5xx, an unmapped 408/425) keeps the intent, so the retry replays the same key and one server effect;
   * every mapped refusal is final for this intent.
   */
  private async route(episodeId: string, intent: IdempotencyIntent, cause: unknown): Promise<void> {
    const error = isAppError(cause) ? cause : this.mapper.from(cause);
    if (isUnknownOutcome(error)) {
      this.errorState.set(error);
      return;
    }
    this.dropIntent(intent);
    if (error.status === 409 && error.code === 'keywords_flagged') {
      // WR6: the editor store is the ONE owner of the flags, and this is the same hand-off the approve flow uses
      // (10.5.6-T1): the rows are mapped once, a refusal whose rows are all unusable writes nothing (no empty list),
      // the episode fence is the editor's, and this store keeps no copy of them.
      this.editor.applyKeywordsFlaggedProblem(error, episodeId);
      this.errorState.set(error);
      return;
    }
    if (error.status === 412) {
      // The ETag this click carried is stale: the retry has to be offered on the fresh one, so the read is awaited
      // (the button stays held while it runs) and no automatic second POST is ever sent.
      this.errorState.set(error);
      this.needsRefreshState.set(true);
      await this.detail.refetch();
      return;
    }
    if (error.status === 409) {
      // The episode moved on (another tab rendered, or it was reopened mid-flight): the refetch re-renders the region
      // from the fresh `nextActions` instead of retrying a command the server has already refused.
      this.errorState.set(error);
      void this.detail.refetch();
      return;
    }
    if (error.status === 429) {
      this.errorState.set(error);
      this.armRateLimit(error.retryAfterSec);
      return;
    }
    if (error.status === 428 || error.status === 422) {
      // Our own bug (a missing `If-Match`, a reused key): the AppError stays exactly as the mapper built it — the view's
      // generic copy is `errorKey`'s — and `traceId` is what support needs. The server's `detail` is never rendered.
      this.errorState.set(error);
      if (isDevMode()) console.warn('render command sent an unusable request', error.code, error.traceId ?? '-');
      return;
    }
    this.errorState.set(error);
  }

  /** Holds the action back for the seconds the server asked for, then releases it (the `RetryCountdown` clock is the view's). */
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

  /** True when the answer belongs to a superseded command, another episode or a torn-down page: it writes nothing. */
  private stale(episodeId: string, call: number): boolean {
    return this.destroyed || call !== this.callSeq || (this.detail.episode()?.id ?? null) !== episodeId;
  }
}
