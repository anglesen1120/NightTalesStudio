import type { EnvironmentProviders, Provider } from '@angular/core';
import type { Routes } from '@angular/router';
import { CatalogRepository } from './application/catalog.repository';
import { SeriesRepository } from './application/series.repository';
import { VOICE_STEP_TRANSLATION_SCOPES } from './application/voice-step.store';
import { CatalogHttpRepository } from './data-access/catalog.http-repository';
import { SeriesHttpRepository } from './data-access/series.http-repository';

/**
 * Providers of the series data path: both ports bound to their HTTP adapters, plus the `series`/`voices`
 * translation scopes (`voices.tag.*` renders wherever a voice row does — owner decision 2026-10-05).
 * The lazy `series` route spreads this into its `providers`; the generated endpoint clients are `providedIn: 'root'`.
 */
export function provideSeriesDataAccess(): Array<Provider | EnvironmentProviders> {
  return [
    { provide: CatalogRepository, useClass: CatalogHttpRepository },
    { provide: SeriesRepository, useClass: SeriesHttpRepository },
    VOICE_STEP_TRANSLATION_SCOPES,
  ];
}

/**
 * The `/series` routes (plan §6.1): the list, the 4-step editor in both modes and the detail shell. Every page is
 * lazy (`loadComponent`) and reads its mode from `data` (the editor) or its id from the path, and every route carries
 * the data-path providers, so a page-scoped store always injects an adapter of this route's own element injector.
 * `new` is declared before `:id`, and `:id/edit` after `:id` (a longer static tail wins by declaration order).
 */
export const SERIES_ROUTES: Routes = [
  {
    path: '',
    loadComponent: () => import('./feature/series-list-page').then((m) => m.SeriesListPage),
    data: { titleKey: 'series.list.title' },
    providers: provideSeriesDataAccess(),
  },
  {
    path: 'new',
    loadComponent: () => import('./feature/series-editor-page').then((m) => m.SeriesEditorPage),
    data: { mode: 'create' },
    providers: provideSeriesDataAccess(),
  },
  {
    path: ':id',
    loadComponent: () => import('./feature/series-detail-page').then((m) => m.SeriesDetailPage),
    providers: provideSeriesDataAccess(),
  },
  {
    path: ':id/edit',
    loadComponent: () => import('./feature/series-editor-page').then((m) => m.SeriesEditorPage),
    data: { mode: 'edit' },
    providers: provideSeriesDataAccess(),
  },
];
