import { Injectable, isDevMode } from '@angular/core';
import type { TranslocoMissingHandler, TranslocoMissingHandlerData } from '@jsverse/transloco';

/**
 * A key absent from the active language renders as its key path — never empty, never `undefined`, and never the
 * other language's text presented as a translation (CI keeps both files equal, so this is the visible-bug path).
 * Development logs each missing key once per language.
 */
@Injectable()
export class MissingKeyHandler implements TranslocoMissingHandler {
  private readonly reported = new Set<string>();

  handle(key: string, data: TranslocoMissingHandlerData): string {
    if (isDevMode()) {
      const id = `${data.activeLang}:${key}`;
      if (!this.reported.has(id)) {
        this.reported.add(id);
        console.warn('missing key', key, data.activeLang);
      }
    }
    return key;
  }
}
