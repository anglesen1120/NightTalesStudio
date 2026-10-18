import type { GuaranteedStage, RenderJob, RenderStatus, RenderTimelineStep, SubStage } from '../domain/render.model';

/**
 * The single stage mapping of the render timeline (WD2/WC2). Pure: no Angular, no rxjs, no i18n service — the i18n
 * keys are resolved by the components, and no component repeats either list below.
 */

/** The order the timeline renders: the three stages every job passes through. */
export const GUARANTEED: readonly GuaranteedStage[] = ['queued', 'running', 'ingesting'];

/** The node sub-steps the adapter may report while `running` (05 CR-2); the one copy of this list in the client. */
export const SUB_STAGES: readonly SubStage[] = ['voice', 'subtitles', 'images', 'assembling', 'checking'];

/**
 * The placeholder for "no job known yet" (an episode that has never rendered, or a `rendering` episode whose job row
 * has not landed). `cancelled` with a blank stage is the shape that reaches nothing: no step is `done`, none is
 * `active` and none is `failed`, so the empty timeline invents no stage. The page shows `LoadingState` in that state.
 */
export const EMPTY_JOB: Pick<RenderJob, 'status' | 'stage' | 'percent' | 'errorCode'> = {
  status: 'cancelled',
  stage: '',
  percent: 0,
  errorCode: null,
};

export function isGuaranteedStage(value: string): value is GuaranteedStage {
  return (GUARANTEED as readonly string[]).includes(value);
}

export function isSubStage(value: string): value is SubStage {
  return (SUB_STAGES as readonly string[]).includes(value);
}

/** `undefined`, `NaN`, `-5` → 0 and `250` → 100: the bar and `aria-valuenow` never leave 0…100. */
export function clampPercent(value: number | null | undefined): number {
  if (value === null || value === undefined || !Number.isFinite(value)) return 0;
  if (value < 0) return 0;
  return value > 100 ? 100 : value;
}

/**
 * The sub-step under `running`, exact match only: `''`, `'queued '`, `'RENDERING'` and `'v2'` are all unknown and
 * yield `null`, so a raw wire stage never reaches a `render.subStep.*` lookup (F3).
 */
export function subStepKeyOf(stage: string): SubStage | null {
  return isSubStage(stage) ? stage : null;
}

/**
 * The three timeline steps of one job, in `GUARANTEED` order (WD2):
 * - `queued` and `submitted` keep `queued` active — `submitted` is the queued→running boundary (`MarkSubmitted` sets
 *   `submitted_at`, the adapter reports `running` afterwards), so the job is not yet rendering;
 * - `ingesting` marks `queued`+`running` done, because the ingest stage implies both; `succeeded` marks all three done;
 * - `failed` marks the stage the job reached as `failed` and every later stage stays `todo`;
 * - `cancelled` marks the reached stage `todo` and never `failed`: `render_cancelled` is a job *state*, not an outcome
 *   (05 README D5), so a cancelled job shows no failure anywhere in the list.
 *
 * The sub-step sits on the `running` step only, and only while that step is the active one.
 */
export function renderTimeline(job: Pick<RenderJob, 'status' | 'stage' | 'percent' | 'errorCode'>): RenderTimelineStep[] {
  const reached = reachedStageIndex(job.status, job.stage);
  const percent = clampPercent(job.percent);
  return GUARANTEED.map((key, index) => {
    const state = stepState(job.status, index, reached);
    return {
      key,
      state,
      percent: state === 'todo' ? null : state === 'done' ? 100 : percent,
      subStepKey: key === 'running' && state === 'active' ? subStepKeyOf(job.stage) : null,
    };
  });
}

/**
 * How far the job got, as an index into `GUARANTEED`. The status decides first (`submitted` is still `queued`;
 * `ingesting`/`succeeded` imply the whole chain); then the reported stage, where a sub-step means `running`. A failed
 * job with no usable stage is taken to have reached `running` — it was executed — while a cancelled job that reported
 * no stage never started, which is what makes `EMPTY_JOB` render three `todo` steps.
 */
function reachedStageIndex(status: RenderStatus, stage: string): number {
  switch (status) {
    case 'queued':
    case 'submitted':
      return 0;
    case 'ingesting':
    case 'succeeded':
      return 2;
    case 'cancelled':
      return stageIndex(stage, 0);
    case 'failed':
      return stageIndex(stage, 1);
    case 'running':
      return stageIndex(stage, 1);
  }
}

function stageIndex(stage: string, fallback: number): number {
  if (isGuaranteedStage(stage)) return GUARANTEED.indexOf(stage);
  return isSubStage(stage) ? 1 : fallback;
}

function stepState(status: RenderStatus, index: number, reached: number): RenderTimelineStep['state'] {
  switch (status) {
    case 'succeeded':
      return 'done';
    case 'failed':
      return index < reached ? 'done' : index === reached ? 'failed' : 'todo';
    case 'cancelled':
      return index < reached ? 'done' : 'todo';
    case 'queued':
    case 'submitted':
    case 'running':
    case 'ingesting':
      return index < reached ? 'done' : index === reached ? 'active' : 'todo';
  }
}
