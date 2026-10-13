import type { CanDeactivateFn } from '@angular/router';
import type { StoryReviewPage } from '../feature/story-review.page';

/**
 * User-initiated navigation and logout only (README D18). The page owns the synchronous `hasUnsavedChanges()` read,
 * the shared `ConfirmDialog` and the shared in-flight prompt, so the guard, the unsaved-work registry and the route
 * deactivation that follows a logout all reach one dialog. A terminal `SessionEnded` never reaches this guard with a
 * draft: it ends the session directly, which tears the page (and its draft) down.
 *
 * The guard itself never mutates the store: the page discards the draft only after the user answers Leave.
 */
export const dirtyStoryGuard: CanDeactivateFn<StoryReviewPage> = (component) => component.confirmLeave();
