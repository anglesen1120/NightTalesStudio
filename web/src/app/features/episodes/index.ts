// Public surface of features/episodes (README D2: a feature imports another only through its `index.ts`).
// `data-access/` and `testing/` are deliberately not re-exported; `provideEpisodesRepository` binds the port to its
// HTTP adapter for another feature's lazy route. The page, list component, store and routes are not re-exported:
// `app.routes.ts` lazy-loads `episodes.routes.ts` directly, and a static re-export would pull the episode list page
// into every consumer's lazy chunk (10.4.1-T1 review PS3). The dashboard (10.4.1-T1) consumes the port, the status
// models, the tone palette and the repository binding.
export { provideEpisodesRepository } from './episodes.providers';
export { EpisodesRepository } from './domain/episodes.repository';
// The render command port (10.6.1-T2): the "Make video" / cancel half of `/episodes/:id`, exposed so a feature that
// drives a render from a route of its own binds the port instead of reaching into `data-access/` (README D2). Its
// adapter is bound by `episodes.routes.ts` on the `:id` route, beside `EpisodesRepository`.
export { RenderRepository } from './domain/render.repository';
// The video-review half of `/episodes/:id` (10.6.2-T1), exposed on the same terms as the render port above: the port
// itself, so a feature that drives a review from a route of its own binds it instead of reaching into `data-access/`
// (README D2). Its adapter is bound where `RenderRepository`'s is — on the `:id` route, through
// `provideVideoReviewRepository()`.
export { VideoReviewRepository } from './domain/video-review.repository';
// `EPISODE_STATUSES` travels with `EpisodeStatus`: the dashboard de-duplicates drifting statuses by this enum order
// (10.4.1-T1 contract item 2), and re-declaring the order there would let the two copies drift apart.
export { EPISODE_STATUSES, type EpisodeListItem, type EpisodeStatus } from './domain/episode.model';
export { EPISODE_STATUS_TONE, type EpisodeStatusTone } from './ui/episode-list.view-model';
// The render half of the domain and the folder's shared scene list (10.6.1-T1): another feature reads a skipped-scene
// list off `EpisodeDetail.renderJob` (10.6.2-T1's review banner) and renders it with this component. The timeline and
// failure components stay private to this feature: only `/episodes/:id` shows them.
export type { RenderImages, RenderJob, SkippedScene } from './domain/render.model';
export { SkippedScenesComponent } from './ui/skipped-scenes/skipped-scenes.component';
