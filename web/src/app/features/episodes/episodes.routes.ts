import type { Routes } from '@angular/router';
import { provideTranslocoScope } from '@jsverse/transloco';
import { EpisodesHttpRepository } from './data-access/episodes-http.repository';
import { SeriesOptionsHttpRepository } from './data-access/series-options-http.repository';
import { EpisodesRepository } from './domain/episodes.repository';
import { SeriesOptionsRepository } from './domain/series-options.repository';

// The repository bindings and the `episodes` scope live on the route (lazy chunk). The page-scoped EpisodeListStore is
// provided on EpisodesListPage itself, so it is destroyed with the component, not cached with the route injector.
// The scope is not prerendered (authenticated shell route), so it is not in bundled-translations.ts.
export const episodesRoutes: Routes = [
  {
    path: '',
    loadComponent: () => import('./feature/episodes-list.page').then((module) => module.EpisodesListPage),
    providers: [
      { provide: EpisodesRepository, useClass: EpisodesHttpRepository },
      { provide: SeriesOptionsRepository, useClass: SeriesOptionsHttpRepository },
      provideTranslocoScope('episodes'),
    ],
  },
];

export const newEpisodeRoutes: Routes = [
  {
    path: '',
    loadComponent: () => import('./feature/new-episode.page').then((module) => module.NewEpisodePage),
    providers: [
      { provide: EpisodesRepository, useClass: EpisodesHttpRepository },
      { provide: SeriesOptionsRepository, useClass: SeriesOptionsHttpRepository },
      provideTranslocoScope('episodes'),
      provideTranslocoScope('story'),
    ],
  },
];
