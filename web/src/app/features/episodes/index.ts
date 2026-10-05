// Public surface of features/episodes. `data-access/` and `testing/` are deliberately not re-exported; the lazy
// `episodes.routes.ts` binds the repositories to their HTTP adapters through `provideEpisodesRepository`. The
// dashboard (10.4.1-T1) consumes the list component and its store, plus the port, the status models, the tone
// palette and the repository binding, from here (README D2: a feature imports another only through its `index.ts`).
export { episodesRoutes } from './episodes.routes';
export { EpisodesListPage } from './feature/episodes-list.page';
export { EpisodeListComponent } from './ui/episode-list.component';
export { EpisodeListStore } from './application/episode-list.store';
export { provideEpisodesRepository } from './episodes.providers';
export { EpisodesRepository } from './domain/episodes.repository';
// `EPISODE_STATUSES` travels with `EpisodeStatus`: the dashboard de-duplicates drifting statuses by this enum order
// (10.4.1-T1 contract item 2), and re-declaring the order there would let the two copies drift apart.
export { EPISODE_STATUSES, type EpisodeListItem, type EpisodeStatus } from './domain/episode.model';
export { EPISODE_STATUS_TONE, type EpisodeStatusTone } from './ui/episode-list.view-model';
