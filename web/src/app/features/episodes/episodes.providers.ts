import type { Provider } from '@angular/core';
import { EpisodesHttpRepository } from './data-access/episodes-http.repository';
import { VideoReviewHttpRepository } from './data-access/video-review-http.repository';
import { EpisodesRepository } from './domain/episodes.repository';
import { VideoReviewRepository } from './domain/video-review.repository';

/**
 * The one binding of the episodes port to its HTTP adapter. `episodes.routes.ts` uses it, and a feature that renders
 * episode rows on a route of its own (the dashboard, 10.4.1-T1) reuses it instead of importing `data-access/` across
 * the feature boundary (README D2). Kept out of `index.ts`'s pure re-exports so the provider's dependency on the
 * generated API client stays behind the feature's public surface.
 */
export function provideEpisodesRepository(): Provider[] {
  return [{ provide: EpisodesRepository, useClass: EpisodesHttpRepository }];
}

/**
 * The binding of the video-review port to its HTTP adapter (10.6.2-T1), beside `RenderRepository` in the `:id` route's
 * providers so the page-scoped `VideoReviewStore` — which the episode page component provides, not the route injector —
 * can inject it. Same shape as the render port: one class binding, the generated client behind `data-access/` (D2/D5).
 */
export function provideVideoReviewRepository(): Provider[] {
  return [{ provide: VideoReviewRepository, useClass: VideoReviewHttpRepository }];
}
