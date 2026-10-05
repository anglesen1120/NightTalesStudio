// No Angular/rxjs/core imports in domain files (README D2). Pure policy helper, no state.
import type { Topic } from './series.model';
import type { StyleTemplate } from './style-template.model';

/**
 * Whether a series of `topic` may use a template with `template.topicHint` — the client mirror of the backend
 * `StyleTopicPolicy.IsAllowed` (`src/NightTales.Domain/Catalog/StyleTopicPolicy.cs`, 03 README D2 / 03 CR-2):
 *
 * - `true_crime` needs a `true_crime` template (`topicHint === 'true_crime'`); `null` is not enough.
 * - `ghost` and `spiritual` accept any template that is not `true_crime`, `topicHint === null` included.
 *
 * The same rule is applied by `GET /style-templates?topic=` as a filter and by the create/update commands as a
 * 400 `validation_failed` on `styleTemplateId`, so this helper is a convenience that keeps the grid honest — it is
 * never the guard (the server re-validates).
 */
export function isTopicCompatible(topic: Topic, template: Pick<StyleTemplate, 'topicHint'>): boolean {
  return topic === 'true_crime' ? template.topicHint === 'true_crime' : template.topicHint !== 'true_crime';
}
