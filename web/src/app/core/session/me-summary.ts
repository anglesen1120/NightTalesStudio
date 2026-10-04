import type { PlatformRole } from './token-pair';

/** The in-memory view of `GET /me`; the shell maps the DTO into it and calls `SessionStore.setMe()`. */
export interface MeSummary {
  readonly uiLocale: 'vi' | 'en';
  readonly role: PlatformRole;
  readonly features: readonly string[];
  readonly displayName: string;
  readonly email: string;
}
