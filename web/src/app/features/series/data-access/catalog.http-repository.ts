import { HttpErrorResponse } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { firstValueFrom } from 'rxjs';
import { AppErrorMapper, type Result } from '@core/errors';
import { CatalogEndpointsClient, type StyleTemplateListItemDto, type VoiceListItemDto } from '@shared/api';
import {
  CatalogRepository,
  type StyleTemplateCatalogResponse,
  type VoiceCatalogResponse,
} from '../application/catalog.repository';
import { safePreviewUrl } from '../domain/preview-url';
import { isTopic, type Topic } from '../domain/series.model';
import { isStyleName, type StyleTemplate } from '../domain/style-template.model';
import { isLanguage, isVoiceTag, type Language, type Voice } from '../domain/voice.model';

/** A plain header token that is a well-formed entity tag; anything else is treated as absent (never echoed anywhere). */
const ETAG_PATTERN = /^(?:W\/)?"[\x21\x23-\x7e]{0,256}"$/;

/** Whether `value` is a well-formed entity tag — the one grammar both the `ETag` header and a body member use. */
export function isEntityTag(value: string | null): boolean {
  return value !== null && ETAG_PATTERN.test(value);
}

/** `ETag` header of a response, or `null` when absent or not a well-formed entity tag. */
export function readEtag(headers: { get(name: string): string | null }): string | null {
  const value = headers.get('ETag');
  return isEntityTag(value) ? value : null;
}

/**
 * DTO -> model, explicit field by field: unknown tags are dropped (a new tag needs a key in both locales, so a raw
 * key must never render), an unsafe `previewUrl` becomes `null` (no-preview path), a language outside `vi|en` drops
 * the voice.
 */
export function toVoice(dto: VoiceListItemDto): Voice | null {
  if (!isLanguage(dto.language)) return null;
  const language: Language = dto.language;
  return {
    id: dto.id,
    displayName: dto.displayName,
    language,
    gender: dto.gender,
    tags: (dto.tags ?? []).filter(isVoiceTag),
    previewUrl: safePreviewUrl(dto.previewUrl),
  };
}

/**
 * DTO -> model, explicit field by field: a `name` that is not a seed slug is dropped (it would be interpolated into
 * an i18n key), an unknown `topicHint` drops the row (the compatibility rule would be judged against garbage) and an
 * unsafe `previewUrl` becomes `null` (the card renders without an image).
 */
export function toStyleTemplate(dto: StyleTemplateListItemDto): StyleTemplate | null {
  if (!isStyleName(dto.name)) return null;
  const hint = dto.topicHint;
  if (hint !== null && hint !== undefined && !isTopic(hint)) return null;
  const topicHint: Topic | null = hint ?? null;
  return { id: dto.id, name: dto.name, topicHint, previewUrl: safePreviewUrl(dto.previewUrl) };
}

/**
 * HTTP adapter of the catalog port: the only file of `features/series` that imports the generated client (D5).
 * One request per call; failures go through `AppErrorMapper` and are returned as `{ ok: false }`, never thrown.
 * The browser's HTTP cache revalidates with `If-None-Match` on its own, so a 304 normally never reaches script; a 304
 * that does (a manually supplied validator) is surfaced as `notModified`.
 */
@Injectable()
export class CatalogHttpRepository extends CatalogRepository {
  private readonly client = inject(CatalogEndpointsClient);
  private readonly mapper = inject(AppErrorMapper);

  override async voices(language: Language, ifNoneMatch?: string | null): Promise<Result<VoiceCatalogResponse>> {
    try {
      const response = await firstValueFrom(this.client.apiV1VoicesGet$Response({ language }));
      const etag = readEtag(response.headers);
      if (ifNoneMatch != null && etag !== null && etag === ifNoneMatch) {
        return { ok: true, value: { kind: 'notModified' } };
      }
      const voices = response.body.items.map(toVoice).filter((voice): voice is Voice => voice !== null);
      return { ok: true, value: { kind: 'ok', voices, etag } };
    } catch (cause) {
      if (cause instanceof HttpErrorResponse && cause.status === 304) {
        return { ok: true, value: { kind: 'notModified' } };
      }
      return { ok: false, error: this.mapper.from(cause) };
    }
  }

  override async styleTemplates(topic: Topic, ifNoneMatch?: string | null): Promise<Result<StyleTemplateCatalogResponse>> {
    try {
      const response = await firstValueFrom(this.client.apiV1StyleTemplatesGet$Response({ topic }));
      const etag = readEtag(response.headers);
      if (ifNoneMatch != null && etag !== null && etag === ifNoneMatch) {
        return { ok: true, value: { kind: 'notModified' } };
      }
      const templates = response.body.items
        .map(toStyleTemplate)
        .filter((template): template is StyleTemplate => template !== null);
      return { ok: true, value: { kind: 'ok', templates, etag } };
    } catch (cause) {
      if (cause instanceof HttpErrorResponse && cause.status === 304) {
        return { ok: true, value: { kind: 'notModified' } };
      }
      return { ok: false, error: this.mapper.from(cause) };
    }
  }
}
