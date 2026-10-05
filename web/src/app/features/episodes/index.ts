// Public surface of features/episodes. `data-access/` and `testing/` are deliberately not re-exported; the lazy
// `episodes.routes.ts` binds the repositories to their HTTP adapters. The dashboard (10.4.1-T1) consumes the list
// component and its store from here.
export { episodesRoutes } from './episodes.routes';
export { EpisodesListPage } from './feature/episodes-list.page';
export { EpisodeListComponent } from './ui/episode-list.component';
export { EpisodeListStore } from './application/episode-list.store';
