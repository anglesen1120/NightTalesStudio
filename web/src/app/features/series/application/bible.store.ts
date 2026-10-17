import { Injectable, computed, inject, signal } from '@angular/core';
import type { AppError, Result } from '@core/errors';
import type { Bible, BibleContent } from '../domain/bible.model';
import { BibleRepository } from './bible.repository';

/**
 * Page-scoped writer of `/series/:id/bible` (10.5.7-T1, README D2): `bible`, its pending proposal, the two write
 * flags and the failure states the panel renders. The route provides it; `reset()` (session end / leaving the page)
 * clears everything and invalidates every in-flight generation.
 *
 * Reads: `load()` is the only trigger — one `GET /series/{id}/bible` per call, no polling and no realtime
 * subscription (D17). The single extra `GET /episodes/{id}` for the proposal banner title is issued only when the
 * loaded bible carries a proposal with a `proposedFromEpisodeId`, at most once per episode id per store lifetime
 * (a failed lookup is not retried), and never when there is no proposal (P5).
 *
 * Writes: `save()` adopts the committed `{ version, etag }` and then refetches so server-assigned entry ids appear;
 * `acceptProposal()`/`dismissProposal()` never send an ETag and refetch on success and on the 409
 * `bible_proposal_not_pending`. A reply whose series/request/proposal generation is stale is dropped.
 */
@Injectable()
export class BibleStore {
  private readonly repository = inject(BibleRepository);

  /** Bumped when the loaded series changes or the store resets; a reply of another series never lands. */
  private seriesGeneration = 0;
  /** Bumped by every load/save/accept/dismiss; only the newest request may publish state. */
  private requestGeneration = 0;
  /** The series the current state belongs to (`null` before the first load and after `reset`). */
  private loadedSeriesId: string | null = null;
  /** The episode id of the one title lookup already attempted, so a refetch never asks for the same title twice. */
  private titleLookupEpisodeId: string | null = null;

  private readonly bibleState = signal<Bible | null>(null);
  private readonly loadingState = signal(false);
  private readonly savingState = signal(false);
  private readonly committingState = signal(false);
  private readonly errorState = signal<AppError | null>(null);
  private readonly fieldErrorsState = signal<Readonly<Record<string, readonly string[]>>>({});
  private readonly conflictState = signal(false);
  private readonly proposedTitleState = signal<string | null>(null);
  private readonly proposalGoneState = signal(false);

  readonly bible = this.bibleState.asReadonly();
  /** The pending proposal, or `null`; derived from `bible` so it cannot drift from the loaded version. */
  readonly proposal = computed(() => this.bibleState()?.proposal ?? null);
  readonly loading = this.loadingState.asReadonly();
  readonly saving = this.savingState.asReadonly();
  /** An accept/dismiss is in flight; a second commit while true is a no-op (one click, one request). */
  readonly committing = this.committingState.asReadonly();
  readonly error = this.errorState.asReadonly();
  /** The `errors` dictionary of a `bible_invalid_content` answer, keys verbatim (`episode_summaries[2].episode_id`). */
  readonly fieldErrors = this.fieldErrorsState.asReadonly();
  /** 412 `bible_conflict`: the form keeps the user's values; the banner offers an explicit reload (D9). */
  readonly conflict = this.conflictState.asReadonly();
  /** The proposing episode title resolved by the one optional lookup; `null` renders `bannerNoTitle`. */
  readonly proposedTitle = this.proposedTitleState.asReadonly();
  /** 409 `bible_proposal_not_pending` on accept/dismiss: the banner renders `series.bible.proposal.none`. */
  readonly proposalGone = this.proposalGoneState.asReadonly();

  /**
   * Loads one series bible. A `404`/`network`/other failure answers through `error` (the page renders `ErrorState`);
   * an empty bible is a successful load, not an error. Switching series clears the previous series' state first, and
   * a reply that arrives after a newer load, a write or `reset()` changes nothing.
   */
  async load(seriesId: string): Promise<void> {
    if (this.loadedSeriesId !== seriesId) {
      this.seriesGeneration += 1;
      this.loadedSeriesId = seriesId;
      this.bibleState.set(null);
      this.titleLookupEpisodeId = null;
      this.proposedTitleState.set(null);
    }
    // A new read supersedes an in-flight write: that write's reply is dropped, so its flag must not stay stuck.
    this.savingState.set(false);
    this.committingState.set(false);
    this.loadingState.set(true);
    this.errorState.set(null);
    this.fieldErrorsState.set({});
    this.conflictState.set(false);
    this.proposalGoneState.set(false);

    const series = this.seriesGeneration;
    const request = ++this.requestGeneration;
    const result = await this.fetchAndAdopt(seriesId, series, request);
    if (!this.isCurrent(request, series)) return;
    this.loadingState.set(false);
    if (!result.ok) this.errorState.set(result.error);
  }

  /**
   * Saves the edited content with the provided ETag and then refetches. Resolves after the refetch, so `bible()`
   * carries the server-assigned ids and the new `version`; the page rebuilds its form from it and only then marks
   * itself clean. A successful save whose refetch fails keeps the adopted `{ version, etag }` and the edited
   * content — the save itself did commit, so no error is raised.
   */
  async save(seriesId: string, content: BibleContent, etag: string): Promise<Result<void>> {
    const series = this.seriesGeneration;
    const request = ++this.requestGeneration;
    this.savingState.set(true);
    this.errorState.set(null);
    this.fieldErrorsState.set({});
    this.conflictState.set(false);

    const result = await this.repository.save(seriesId, content, etag);
    if (!this.isCurrent(request, series)) return result.ok ? { ok: true, value: undefined } : result;
    if (!result.ok) {
      this.savingState.set(false);
      this.applyFailure(result.error);
      return result;
    }
    const current = this.bibleState();
    if (current !== null && current.seriesId === seriesId) {
      // The committed `{ version, etag }` of the response plus the content that was just stored: if the refetch
      // below fails, the page still carries the edits it saved. The refetch stays authoritative for server ids.
      // An empty write etag (the mapper's answer when neither the ETag header nor the body carries a valid tag) is
      // never adopted: the owner-approved tag is the quoted `"sb-<version>"`, so the next If-Match stays valid even
      // when the refetch fails.
      const committedEtag = result.value.etag || `"sb-${result.value.version}"`;
      this.bibleState.set({ ...current, content, version: result.value.version, etag: committedEtag });
    }
    await this.fetchAndAdopt(seriesId, series, request);
    if (this.isCurrent(request, series)) this.savingState.set(false);
    return { ok: true, value: undefined };
  }

  /**
   * Accepts the pending proposal (no `If-Match`) and refetches. A second call while one is in flight is a no-op that
   * reports success without a request. A 409 `bible_proposal_not_pending` sets `proposalGone`, refetches and returns
   * the failure — the page renders `series.bible.proposal.none`, not an error state.
   */
  async acceptProposal(seriesId: string): Promise<Result<void>> {
    return this.commit(seriesId, () => this.repository.accept(seriesId));
  }

  /** Dismisses the pending proposal (no `If-Match`) with the same commit rules as `acceptProposal()`. */
  async dismissProposal(seriesId: string): Promise<Result<void>> {
    return this.commit(seriesId, () => this.repository.dismiss(seriesId));
  }

  /** Clears every signal and invalidates in-flight loads, saves, commits and title lookups. */
  reset(): void {
    this.seriesGeneration += 1;
    this.requestGeneration += 1;
    this.loadedSeriesId = null;
    this.titleLookupEpisodeId = null;
    this.bibleState.set(null);
    this.loadingState.set(false);
    this.savingState.set(false);
    this.committingState.set(false);
    this.errorState.set(null);
    this.fieldErrorsState.set({});
    this.conflictState.set(false);
    this.proposedTitleState.set(null);
    this.proposalGoneState.set(false);
  }

  private async commit(seriesId: string, action: () => Promise<Result<void>>): Promise<Result<void>> {
    if (this.committingState()) return { ok: true, value: undefined };
    const series = this.seriesGeneration;
    const request = ++this.requestGeneration;
    this.committingState.set(true);
    this.errorState.set(null);
    this.fieldErrorsState.set({});
    this.conflictState.set(false);
    this.proposalGoneState.set(false);

    const result = await action();
    if (!this.isCurrent(request, series)) return result;
    if (result.ok) {
      await this.fetchAndAdopt(seriesId, series, request);
      if (this.isCurrent(request, series)) this.committingState.set(false);
      return { ok: true, value: undefined };
    }
    if (result.error.code === 'bible_proposal_not_pending') {
      this.proposalGoneState.set(true);
      await this.fetchAndAdopt(seriesId, series, request);
      if (this.isCurrent(request, series)) this.committingState.set(false);
      return result;
    }
    this.committingState.set(false);
    this.errorState.set(result.error);
    return result;
  }

  /**
   * One `GET /series/{id}/bible` that publishes only while its generation is current. The optional title lookup starts
   * after the publish and is NOT awaited: `load()`, `save()` and `commit()` clear their flags on the bible alone, so a
   * slow `GET /episodes/{id}` never keeps the page loading, saving or committing.
   */
  private async fetchAndAdopt(seriesId: string, series: number, request: number): Promise<Result<Bible>> {
    const result = await this.repository.get(seriesId);
    if (!this.isCurrent(request, series)) return result;
    if (!result.ok) return result;
    this.bibleState.set(result.value);
    this.startProposedTitleLookup(result.value, series);
    return result;
  }

  /**
   * The one title lookup of a pending proposal (P5), fire-and-forget. It runs only for a proposal with a
   * `proposedFromEpisodeId`, at most once per episode id: a refetch after a save does not ask again, and a failure is
   * never retried or surfaced. A result of a superseded series/proposal is dropped.
   */
  private startProposedTitleLookup(bible: Bible, series: number): void {
    const episodeId = bible.proposal?.proposedFromEpisodeId ?? null;
    if (bible.proposal === null || episodeId === null) {
      this.proposedTitleState.set(null);
      this.titleLookupEpisodeId = null;
      return;
    }
    if (this.titleLookupEpisodeId === episodeId) return;
    this.titleLookupEpisodeId = episodeId;
    // A title of the previous proposal never stays on the banner while the new episode's title is still in flight.
    this.proposedTitleState.set(null);
    void this.lookUpProposedTitle(episodeId, series);
  }

  /** Awaits the title and publishes it only if its series and proposal are still current; every failure is silent. */
  private async lookUpProposedTitle(episodeId: string, series: number): Promise<void> {
    try {
      const title = await this.repository.getEpisodeTitle(episodeId);
      if (series !== this.seriesGeneration || this.titleLookupEpisodeId !== episodeId) return;
      this.proposedTitleState.set(title);
    } catch {
      // P5: a failed lookup keeps the banner's no-title copy and is never retried.
    }
  }

  /** Routes a failed write to the state its copy needs; the conflict never touches `bible` (acceptance: 412). */
  private applyFailure(error: AppError): void {
    if (error.status === 412 || error.code === 'bible_conflict' || error.kind === 'conflict') {
      this.conflictState.set(true);
      return;
    }
    if (error.code === 'bible_invalid_content' || error.kind === 'validation') {
      this.fieldErrorsState.set(error.fieldErrors);
      // The page binds this `AppError` (D9 `bindFieldErrors`) and renders `series.bible.error.limits` plus the
      // form-level alert for the pointers no control owns; it is not a page-level error state.
      this.errorState.set(error);
      return;
    }
    // 428 `if_match_required` is a client bug (`AppErrorMapper` already warns in dev), 404/network/5xx a page error.
    this.errorState.set(error);
  }

  private isCurrent(request: number, series: number): boolean {
    return request === this.requestGeneration && series === this.seriesGeneration;
  }
}
