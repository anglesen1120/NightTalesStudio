import type { AppLocale } from '@core/i18n';
import type { MeSummary } from './me-summary';

/**
 * The profile port (README D5/D14). Both calls resolve the mapped `MeSummary` and reject with an `AppError` (built by
 * `AppErrorMapper`). `updateUiLocale` sends exactly `{ uiLocale }` — the endpoint's strict binding rejects any other
 * member — and resolves the profile the server returns.
 */
export abstract class MeRepository {
  abstract get(): Promise<MeSummary>;
  abstract updateUiLocale(locale: AppLocale): Promise<MeSummary>;
}
