import type { CanDeactivateFn } from '@angular/router';
import type { StoryReviewPage } from '../feature/story-review.page';

/**
 * User-initiated navigation and logout only (README D18). The page owns the synchronous `hasUnsavedChanges()` read,
 * the shared `ConfirmDialog` and the shared in-flight prompt, so the guard, the unsaved-work registry and the route
 * deactivation that follows a logout all reach one dialog. A terminal `SessionEnded` never reaches this guard with a
 * draft: `SessionStore.endSession` discards it through the unsaved-work registry before the navigation that tears the
 * page down, so the guard finds a clean draft and opens no dialog.
 *
 * The guard itself never mutates the store: the page discards the draft after the user answers Leave, or immediately
 * on a terminal session end.
 */
export const dirtyStoryGuard: CanDeactivateFn<StoryReviewPage> = (component) => component.confirmLeave();
