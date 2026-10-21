import type { Result } from '@core/errors';
import type { Topic } from '../domain/series.model';
import type { StyleTemplate } from '../domain/style-template.model';
import type { Language, Voice } from '../domain/voice.model';

/** The outcome of a voices read: a fresh list, or "unchanged since the representation the caller holds" (304). */
export type VoiceCatalogResponse =
  | { readonly kind: 'ok'; readonly voices: readonly Voice[]; readonly etag: string | null }
  | { readonly kind: 'notModified' };

/** The outcome of a style-template read, with the same 304 discipline as `VoiceCatalogResponse`. */
export type StyleTemplateCatalogResponse =
  | { readonly kind: 'ok'; readonly templates: readonly StyleTemplate[]; readonly etag: string | null }
  | { readonly kind: 'notModified' };

/**
 * The series feature's port over the public catalog (`GET /api/v1/voices`, `GET /api/v1/style-templates`). Declared
 * here, not in `domain/`, because the core-wide `Result<T>` lives in `@core/errors` and domain may not import
 * `@core` (lint D2) — the same placement as `AuthRepository`. The HTTP adapter is `data-access/catalog.http-repository.ts`;
 * the scripted fake is `testing/fake-catalog.repository.ts`.
 *
 * `ifNoneMatch` is the ETag the caller last saw for this language/topic (never exposed to presentation); a
 * `notModified` answer tells the store to keep the list it already holds.
 */
export abstract class CatalogRepository {
  abstract voices(language: Language, ifNoneMatch?: string | null): Promise<Result<VoiceCatalogResponse>>;
  abstract styleTemplates(topic: Topic, ifNoneMatch?: string | null): Promise<Result<StyleTemplateCatalogResponse>>;
}
