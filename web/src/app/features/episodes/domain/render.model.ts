/**
 * The render half of the episode domain (10.6.1-T1): the job the timeline renders, the scenes the image pipeline
 * dropped, and the failure the panel reports.
 *
 * Domain rules (D2): no Angular, no rxjs. Every field is either a closed wire value or a value the mapper has already
 * validated (F5/F6), so nothing in this file is ever rendered raw: an unknown stage string is stored verbatim and only
 * ever passes through `subStepKeyOf`/`renderTimeline`, and an unknown failure code only through `failureCopy`.
 */

/** The closed `render_jobs.status` set (09.6.1-T1, 09.6.3-T1, 09.6.4-T1). */
export type RenderStatus = 'queued' | 'submitted' | 'running' | 'ingesting' | 'succeeded' | 'failed' | 'cancelled';

/** The closed set as a value, for the mapper's validation (mirrors `LIST_STATUSES`): no other status is known. */
export const RENDER_STATUSES: readonly RenderStatus[] = ['queued', 'submitted', 'running', 'ingesting', 'succeeded', 'failed', 'cancelled'];

/** The three stages every job passes through, in order (WD2/WC2). The finer ones are `SubStage`s under `running`. */
export type GuaranteedStage = 'queued' | 'running' | 'ingesting';

/** The node sub-steps of `running` (05 CR-2 / 00 §8); an unknown value is dropped, never rendered (F3). */
export type SubStage = 'voice' | 'subtitles' | 'images' | 'assembling' | 'checking';

/** One scene the image pipeline did not produce (03 D7): `reason ∈ moderated | rate_limited | relay_rejected | timeout | upstream_error`. */
export interface SkippedScene {
  segment: string;
  reason: string;
}

/** `renderJob.images` (03.4.2-T1, soft — O-2): absent from the wire until that task lands, hence nullable on the job. */
export interface RenderImages {
  needed: number;
  succeeded: number;
  skipped: readonly SkippedScene[];
}

/** One render job as `GET /episodes/{id}` reports it plus whatever the live progress signal adds (F5/F6/WD2). */
export interface RenderJob {
  renderJobId: string;
  status: RenderStatus;
  /** Stored verbatim; an unknown value is never rendered (`subStepKeyOf`/`renderTimeline` fail closed). */
  stage: string;
  percent: number;
  attempt: number;
  imagesNeeded: number;
  submittedAt: string | null;
  finishedAt: string | null;
  errorCode: string | null;
  hints: readonly string[];
  images: RenderImages | null;
}

/** One row of the ordered stage list: `percent` is `null` while the stage is `todo`, `100` once it is `done`. */
export interface RenderTimelineStep {
  key: GuaranteedStage;
  state: 'done' | 'active' | 'todo' | 'failed';
  percent: number | null;
  subStepKey: SubStage | null;
}

/** A job's failure as the panel needs it: the code, the scenes it names, and the image-side detail code, if any. */
export interface RenderFailure {
  code: string;
  scenes: readonly SkippedScene[];
  detailCode: string | null;
}

/** The actions a failure panel may offer (F7): reopen the story on the named scenes, or make the video again. */
export type FailureAction = 'editScenes' | 'retry';

/** Resolved copy of one failure: i18n keys only, never server prose (D9/D10). */
export interface FailureCopy {
  titleKey: string;
  bodyKeys: readonly string[];
  actionKeys: readonly FailureAction[];
}
