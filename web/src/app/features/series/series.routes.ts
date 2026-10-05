import type { EnvironmentProviders, Provider } from '@angular/core';
import type { Routes } from '@angular/router';
import { CatalogRepository } from './application/catalog.repository';
import { VOICE_STEP_TRANSLATION_SCOPES } from './application/voice-step.store';
import { CatalogHttpRepository } from './data-access/catalog.http-repository';

/**
 * Providers of the series data path: the catalog port bound to its HTTP adapter, plus the `series` and `voices`
 * translation scopes (`voices.tag.*` renders wherever a voice row does - owner decision 2026-10-05).
 * 10.4.2-T1 spreads this into its lazy route's `providers`; the generated `CatalogEndpointsClient` is `providedIn: 'root'`.
 */
export function provideSeriesDataAccess(): Array<Provider | EnvironmentProviders> {
  return [{ provide: CatalogRepository, useClass: CatalogHttpRepository }, VOICE_STEP_TRANSLATION_SCOPES];
}

/** The series pages are added by 10.4.2-T1; this task only ships the data access above. */
export const SERIES_ROUTES: Routes = [];
