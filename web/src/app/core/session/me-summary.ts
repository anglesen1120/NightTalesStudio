import type { PlatformRole } from './token-pair';

/** The feature flags the client knows (README D14); the server may advertise more, they are dropped at the mapping. */
export const FEATURE_KEYS = ['source_finder', 'youtube_publish'] as const;
export type FeatureKey = (typeof FEATURE_KEYS)[number];

/** The in-memory view of `GET /me`; `MeRepository` maps the DTO into it and the shell calls `SessionStore.setMe()`. */
export interface MeSummary {
  readonly id: string;
  readonly uiLocale: 'vi' | 'en';
  readonly role: PlatformRole;
  readonly features: readonly FeatureKey[];
  readonly displayName: string;
  readonly email: string;
}
