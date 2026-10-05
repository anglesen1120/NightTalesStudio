import type { Provider } from '@angular/core';
import { EpisodesHttpRepository } from './data-access/episodes-http.repository';
import { EpisodesRepository } from './domain/episodes.repository';

/**
 * The one binding of the episodes port to its HTTP adapter. `episodes.routes.ts` uses it, and a feature that renders
 * episode rows on a route of its own (the dashboard, 10.4.1-T1) reuses it instead of importing `data-access/` across
 * the feature boundary (README D2). Kept out of `index.ts`'s pure re-exports so the provider's dependency on the
 * generated API client stays behind the feature's public surface.
 */
export function provideEpisodesRepository(): Provider[] {
  return [{ provide: EpisodesRepository, useClass: EpisodesHttpRepository }];
}
