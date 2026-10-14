import type { CanDeactivateFn } from '@angular/router';
import type { ResearchPageComponent } from '../feature/research-page/research-page.component';

/**
 * User-initiated navigation and logout only (README D18). The page owns the synchronous `hasUnsavedChanges()` read,
 * the shared `ConfirmDialog` and the in-flight prompt, so the guard, the unsaved-work registry and the route
 * deactivation that follows a logout all reach one dialog — exactly as the story editor's `dirtyStoryGuard` does.
 */
export const dirtyResearchGuard: CanDeactivateFn<ResearchPageComponent> = (component) => component.confirmLeave();
