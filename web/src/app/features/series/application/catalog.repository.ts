import type { Result } from '@core/errors';
import type { Language, Voice } from '../domain/voice.model';

/** The outcome of a voices read: a fresh list, or "unchanged since the representation the caller holds" (304). */
export type VoiceCatalogResponse =
  | { readonly kind: 'ok'; readonly voices: readonly Voice[]; readonly etag: string | null }
  | { readonly kind: 'notModified' };

/**
 * The series feature's port over the public catalog (GET /api/v1/voices). Declared here, not in `domain/`, because the
 * core-wide `Result<T>` lives in `@core/errors` and domain may not import `@core` (lint D2) - the same placement as
 * `AuthRepository`. The HTTP adapter is `data-access/catalog.http-repository.ts`; the scripted fake is
 * `testing/fake-catalog.repository.ts`. The `styleTemplates(topic)` half of the port is added by 10.4.2-T1.
 *
 * `ifNoneMatch` is the ETag the caller last saw for this language (never exposed to presentation); a `notModified`
 * answer tells the store to keep the list it already holds.
 */
export abstract class CatalogRepository {
  abstract voices(language: Language, ifNoneMatch?: string | null): Promise<Result<VoiceCatalogResponse>>;
}
