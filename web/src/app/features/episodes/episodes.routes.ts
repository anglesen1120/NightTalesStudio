import type { Routes } from '@angular/router';
import { provideTranslocoScope } from '@jsverse/transloco';
import { EpisodesHttpRepository } from './data-access/episodes-http.repository';
import { StoryHttpRepository } from './data-access/story-http.repository';
import { HttpStoryVersionRepository } from './data-access/http-story-version.repository';
import { HttpResearchRepository } from './data-access/http-research.repository';
import { HttpSourceFinderRepository } from './data-access/http-source-finder.repository';
import { SeriesOptionsHttpRepository } from './data-access/series-options-http.repository';
import { RenderHttpRepository } from './data-access/render-http.repository';
import { dirtyStoryGuard } from './guards/dirty-story.guard';
import { dirtyResearchGuard } from './guards/dirty-research.guard';
import { EpisodesRepository } from './domain/episodes.repository';
import { StoryRepository } from './domain/story.repository';
import { StoryVersionRepository } from './domain/story-version.repository';
import { ResearchRepository } from './domain/research.repository';
import { SeriesOptionsRepository } from './domain/series-options.repository';
import { SourceFinderRepository } from './domain/source-finder.repository';
import { RenderRepository } from './domain/render.repository';

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
  {
    // A complete sibling path; Angular backtracks when `:id` cannot consume the remaining segment.
    path: ':id/research',
    loadComponent: () => import('./feature/research-page/research-page.component').then((module) => module.ResearchPageComponent),
    canDeactivate: [dirtyResearchGuard],
    providers: [
      { provide: EpisodesRepository, useClass: EpisodesHttpRepository },
      { provide: SeriesOptionsRepository, useClass: SeriesOptionsHttpRepository },
      { provide: ResearchRepository, useClass: HttpResearchRepository },
      { provide: SourceFinderRepository, useClass: HttpSourceFinderRepository },
      provideTranslocoScope('episodes'),
      provideTranslocoScope('story'),
    ],
  },
  {
    path: ':id',
    loadComponent: () => import('./feature/story-review.page').then((module) => module.StoryReviewPage),
    canDeactivate: [dirtyStoryGuard],
    providers: [
      { provide: EpisodesRepository, useClass: EpisodesHttpRepository },
      { provide: StoryRepository, useClass: StoryHttpRepository },
      { provide: StoryVersionRepository, useClass: HttpStoryVersionRepository },
      { provide: SeriesOptionsRepository, useClass: SeriesOptionsHttpRepository },
      { provide: ResearchRepository, useClass: HttpResearchRepository },
      { provide: RenderRepository, useClass: RenderHttpRepository },
      provideTranslocoScope('episodes'),
      provideTranslocoScope('story'),
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
