import { Injectable, inject, isDevMode } from '@angular/core';
import { firstValueFrom } from 'rxjs';
import { AppErrorMapper } from '@core/errors';
import { isAppLocale, type AppLocale } from '@core/i18n';
import { MeEndpointsClient, type MeDto, type UpdateMeRequest } from '@shared/api';
import { FEATURE_KEYS, type FeatureKey, type MeSummary } from '../me-summary';
import { MeRepository } from '../me.repository';

function isFeatureKey(value: unknown): value is FeatureKey {
  return (FEATURE_KEYS as readonly unknown[]).includes(value);
}

/**
 * `MeDto` → `MeSummary`. `features` is defensive to a missing member (`?? []`) and filtered through the allow-list, so
 * an unknown key is dropped; the server's `uiLocale` is authoritative, an unknown value falls back to `vi` with one
 * dev-only warning.
 */
export function toMeSummary(dto: MeDto): MeSummary {
  const features = (dto.features as readonly unknown[] | undefined) ?? [];
  const raw: unknown = dto.uiLocale;
  let uiLocale: AppLocale = 'vi';
  if (isAppLocale(raw)) {
    uiLocale = raw;
  } else if (isDevMode()) {
    console.warn('MeDto.uiLocale is not a supported language; falling back to vi.');
  }
  return {
    id: dto.id,
    displayName: dto.displayName,
    email: dto.email,
    role: dto.role,
    uiLocale,
    features: features.filter(isFeatureKey),
  };
}

/**
 * `GET`/`PATCH /api/v1/me` through the generated client — the only file importing `@shared/api` for `/me` (lint D5).
 * Both are plain background-or-action calls with no idempotency intent; the PATCH body is exactly `{ uiLocale }`.
 */
@Injectable()
export class MeHttpRepository extends MeRepository {
  private readonly client = inject(MeEndpointsClient);
  private readonly mapper = inject(AppErrorMapper);

  override async get(): Promise<MeSummary> {
    try {
      return toMeSummary(await firstValueFrom(this.client.apiV1MeGet()));
    } catch (cause) {
      // eslint-disable-next-line @typescript-eslint/only-throw-error -- the MeRepository port rejects with an AppError
      throw this.mapper.from(cause);
    }
  }

  override async updateUiLocale(locale: AppLocale): Promise<MeSummary> {
    try {
      // The generated type lists `displayName` as required-nullable; the endpoint's strict binding wants `{ uiLocale }` only.
      const body = { uiLocale: locale } as UpdateMeRequest;
      return toMeSummary(await firstValueFrom(this.client.apiV1MePatch({ body })));
    } catch (cause) {
      // eslint-disable-next-line @typescript-eslint/only-throw-error -- the MeRepository port rejects with an AppError
      throw this.mapper.from(cause);
    }
  }
}
