import type { FailureAction, FailureCopy, RenderJob, SkippedScene } from '../domain/render.model';

/**
 * Failure copy resolution for the render panel (F7/D9/D10). Pure: no Angular, no i18n service — it returns keys, and
 * the panel resolves them; the server's `detail` prose is never read or shown, and no code from the wire is ever
 * rendered as text.
 */

/**
 * The nine F7 job outcomes that own an `errors.<code>` title (05 README D5). Each one is a `render_jobs.error_code`;
 * `render_cancelled` is deliberately absent because it is a job *state* and never an outcome.
 */
export const JOB_FAILURE_CODES: readonly string[] = [
  'tts_failed',
  'image_moderated',
  'image_generation_failed',
  'render_assembly_failed',
  'render_node_error',
  'render_lost',
  'render_timeout',
  'render_output_invalid',
  'render_quality_failed',
];

/**
 * The three image-outcome codes of plan §6 that have a `render.failure.code.<code>` body and **no** `errors.<code>`
 * title (they are not in README D9's shared error catalogue), so they borrow the generic title instead of asking for a
 * key that does not exist.
 */
export const BODY_ONLY_CODES: readonly string[] = ['images_incomplete', 'provider_funds_exhausted', 'upstream_error'];

/** The outcomes a second attempt can fix (F7); `render_quality_failed` is offered too — its output is deterministic,
 *  but the server's `nextActions` is what finally decides whether the button is enabled. */
const RETRY_CODES: readonly string[] = [
  'tts_failed',
  'render_assembly_failed',
  'render_node_error',
  'render_lost',
  'render_timeout',
  'render_output_invalid',
  'render_quality_failed',
];

/** The moderated-image outcomes: the named scenes are what blocked the render, so fixing them is the useful action. */
const EDIT_SCENES_CODES: readonly string[] = ['image_moderated', 'image_generation_failed'];

/** The generic title, for a code this client does not know and for the body-only codes: never a raw code (D9). */
export const GENERIC_TITLE_KEY = 'render.failure.title';

/**
 * Resolves one failure to i18n keys: the code's own title when it has one, its body key when it has one, and the
 * actions that fit it. An unknown code — or the job state `render_cancelled` — gets the generic title and no action.
 * `Edit scenes` is offered only when a scene of *this* job was `moderated`: a skip for a transient reason names no
 * scene the owner could fix.
 */
export function failureCopy(code: string, scenes: readonly SkippedScene[]): FailureCopy {
  return {
    titleKey: JOB_FAILURE_CODES.includes(code) ? `errors.${code}` : GENERIC_TITLE_KEY,
    bodyKeys: BODY_ONLY_CODES.includes(code) ? [`render.failure.code.${code}`] : [],
    actionKeys: actionKeys(code, scenes),
  };
}

function actionKeys(code: string, scenes: readonly SkippedScene[]): readonly FailureAction[] {
  if (EDIT_SCENES_CODES.includes(code) && scenes.some((scene) => scene.reason === 'moderated')) return ['editScenes'];
  return RETRY_CODES.includes(code) ? ['retry'] : [];
}

/**
 * The image-side detail of a failed job: the first skip reason that is not `moderated` (`rate_limited`,
 * `relay_rejected`, `timeout`, `upstream_error` — 03 D7), or `null` when every skipped scene was moderated or there is
 * nothing to read. It is a *code*: the scene list resolves each reason to a `render.images.reason.*` key, and this
 * value is never rendered as prose.
 */
export function imageDetailCode(job: RenderJob | null): string | null {
  const skipped = job?.images?.skipped ?? [];
  const other = skipped.find((scene) => scene.reason !== 'moderated');
  return other === undefined ? null : other.reason;
}
