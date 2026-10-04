import { isPlatformServer } from '@angular/common';
import { HttpClient } from '@angular/common/http';
import { Injectable, PLATFORM_ID, inject, isDevMode } from '@angular/core';
import type { Translation, TranslocoLoader, TranslocoLoaderData } from '@jsverse/transloco';
import { Observable, catchError, from, map, of, shareReplay, switchMap, throwError } from 'rxjs';
import { isAppLocale } from './locale.model';

const ROOT_SCOPE = 'core';
/** A scope is a kebab-case directory name under `/assets/i18n/`; anything else (`..`, `/`, `__proto__`) never reaches a URL. */
const SCOPE_PATTERN = /^[a-z0-9]+(-[a-z0-9]+)*$/;

/**
 * Loads `/assets/i18n/<scope>/<lang>.json`. The URL is root-relative on purpose: the production document has no
 * `<base>` (CSP `base-uri 'none'`), so a relative URL would 404 from a nested route.
 *
 * - One request per `scope/lang` while it succeeds; a failed load is evicted from the cache and rethrown, so the
 *   next activation or scope load retries the request instead of serving `{}` until reload.
 * - A scope or language that is not well-formed (`SCOPE_PATTERN`, `vi|en`) resolves to `{}` without any request.
 * - On the server (prerender) the bundled translation is used and no HTTP request is made (README D12/F18).
 * - The result is an Observable, not a Promise: the response reaches Transloco (and the zoneless scheduler) in the
 *   same task that delivered it, so `whenStable()` covers the template update.
 */
@Injectable({ providedIn: 'root' })
export class TranslocoHttpLoader implements TranslocoLoader {
  private readonly http = inject(HttpClient);
  private readonly server = isPlatformServer(inject(PLATFORM_ID));
  private readonly cache = new Map<string, Observable<Translation>>();

  /** Transloco passes a scoped path (`quality/vi`) as `lang` together with `data.scope`; the language is the last segment. */
  getTranslation(lang: string, data?: TranslocoLoaderData): Observable<Translation> {
    const scope = data?.scope ?? ROOT_SCOPE;
    const language = lang.slice(lang.lastIndexOf('/') + 1);
    if (!SCOPE_PATTERN.test(scope) || !isAppLocale(language)) {
      if (isDevMode()) console.error('i18n scope rejected', scope, language);
      return of<Translation>({});
    }
    const cacheKey = `${scope}/${language}`;
    let translation = this.cache.get(cacheKey);
    if (translation === undefined) {
      translation = this.load(scope, language).pipe(
        catchError((error: unknown) => {
          this.cache.delete(cacheKey);
          if (isDevMode()) console.error('i18n scope failed', scope, language);
          return throwError(() => error);
        }),
        shareReplay(1),
      );
      this.cache.set(cacheKey, translation);
    }
    return translation;
  }

  private load(scope: string, language: string): Observable<Translation> {
    if (!this.server) {
      return this.http.get<Translation>(`/assets/i18n/${scope}/${language}.json`);
    }
    // Dynamic so the bundled JSON stays out of the browser's initial chunk (only the server platform asks).
    return from(import('./bundled-translations')).pipe(
      map(({ bundledFor }) => bundledFor(scope, language)),
      switchMap((bundled) =>
        bundled === undefined ? throwError(() => new Error(`scope ${scope} is not bundled for prerender`)) : of(bundled),
      ),
    );
  }
}
