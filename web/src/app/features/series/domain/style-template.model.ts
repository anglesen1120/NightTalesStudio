// No Angular/rxjs/core imports in domain files (README D2). Pure model of the style catalog.
import type { Topic } from './series.model';

/**
 * One row of `GET /api/v1/style-templates` (09.4.2-T1 §5): `{ id, name, topicHint, previewUrl }`. `name` is the
 * seed slug whose label/hint live at `series.style.<name>.label|.hint`; `previewUrl` is already sanitised by the
 * repository mapper (`safePreviewUrl`), so a missing or unsafe URL is simply `null` (no preview card image).
 */
export interface StyleTemplate {
  readonly id: string;
  readonly name: string;
  readonly topicHint: Topic | null;
  readonly previewUrl: string | null;
}

/** A seed slug: lowercase alphanumerics in kebab case (`dark-folklore-ink`). Never a translated string. */
const STYLE_NAME_PATTERN = /^[a-z0-9]+(?:-[a-z0-9]+)*$/;

/**
 * True for a wire `name` that can be turned into an i18n key. A value outside this shape must be dropped by the
 * mapper: interpolating it into `series.style.<name>.label` would render a raw or hostile key (README D10).
 */
export function isStyleName(name: string): boolean {
  return name.length <= 64 && STYLE_NAME_PATTERN.test(name);
}

/** The label key of a template name — the only place the key is built (03 CR-7). */
export function styleLabelKey(name: string): string {
  return `series.style.${name}.label`;
}

/** The one-line hint key of a template name — the only place the key is built (03 CR-7). */
export function styleHintKey(name: string): string {
  return `series.style.${name}.hint`;
}

/** Announced when a topic change cleared a style the new topic cannot use (U6, 03 CR-7). */
export const STYLE_CLEARED_BY_TOPIC_KEY = 'series.style.clearedByTopic';

/** The fallback card of a template a series still references after it was de-activated (09.4.2-T1:147). */
export const STYLE_INACTIVE_KEY = 'series.style.inactive';
