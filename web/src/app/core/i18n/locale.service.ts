import { DOCUMENT } from '@angular/common';
import { Injectable, inject, signal } from '@angular/core';
import { TranslocoService } from '@jsverse/transloco';
import { PreferencesStore } from '@core/prefs';
import { DEFAULT_LOCALE, LOCALE_TAG, isAppLocale, type AppLocale } from './locale.model';

type Instant = Date | number | string;

export interface RelativeOptions {
  /** The reference instant; defaults to now. */
  readonly now?: Instant;
  /** `auto` renders "tomorrow"/"ngày mai" where the language has a word for it. */
  readonly numeric?: Intl.RelativeTimeFormatNumeric;
}

const DATE_OPTIONS: Intl.DateTimeFormatOptions = { day: '2-digit', month: '2-digit', year: 'numeric' };
const DATE_TIME_OPTIONS: Intl.DateTimeFormatOptions = { dateStyle: 'medium', timeStyle: 'short' };
const ZONED_OPTIONS: Intl.DateTimeFormatOptions = {
  day: '2-digit',
  month: '2-digit',
  year: 'numeric',
  hour: '2-digit',
  minute: '2-digit',
  hourCycle: 'h23',
};

/** Largest unit whose absolute size the delta reaches, in milliseconds. */
const RELATIVE_UNITS: readonly (readonly [Intl.RelativeTimeFormatUnit, number])[] = [
  ['year', 365 * 24 * 3_600_000],
  ['month', 30 * 24 * 3_600_000],
  ['week', 7 * 24 * 3_600_000],
  ['day', 24 * 3_600_000],
  ['hour', 3_600_000],
  ['minute', 60_000],
];

/**
 * The one place that owns the UI language at runtime (README D10): Transloco's active language, `<html lang>`,
 * and every `Intl` format. No screen calls `Intl` directly. Locale persistence to the profile (`PATCH /me`) lives
 * in the shell store (10.2.6-T1), so this root service never depends on a route-provided repository.
 */
@Injectable({ providedIn: 'root' })
export class LocaleService {
  private readonly transloco = inject(TranslocoService);
  private readonly prefs = inject(PreferencesStore);
  private readonly document = inject(DOCUMENT);

  private readonly localeState = signal<AppLocale>(DEFAULT_LOCALE);
  readonly locale = this.localeState.asReadonly();

  // One formatter per tag + options for the app's lifetime: no `Intl` object is constructed per render.
  private readonly dateFormats = new Map<string, Intl.DateTimeFormat>();
  private readonly numberFormats = new Map<string, Intl.NumberFormat>();
  private readonly relativeFormats = new Map<string, Intl.RelativeTimeFormat>();

  /**
   * First defined of: the profile's `uiLocale`, the saved pre-login preference, the first browser tag whose
   * primary subtag is `vi`/`en`, then `vi`. Unsupported values at any step are skipped.
   */
  resolveInitial(profile: unknown, pref: unknown, browserLangs: readonly string[]): AppLocale {
    if (isAppLocale(profile)) return profile;
    if (isAppLocale(pref)) return pref;
    for (const tag of browserLangs) {
      const primary = tag.trim().toLowerCase().split('-')[0];
      if (isAppLocale(primary)) return primary;
    }
    return DEFAULT_LOCALE;
  }

  /**
   * Switches the language everywhere that is local and synchronous: Transloco, `<html lang>`, the locale signal
   * and — only with `persist: true` — the saved preference (the one `core/prefs` write). No await, no request.
   */
  activate(locale: AppLocale, options: { readonly persist: boolean }): void {
    this.transloco.setActiveLang(locale);
    this.document.documentElement.lang = locale;
    this.localeState.set(locale);
    if (options.persist) this.prefs.setLanguage(locale);
  }

  /** `dd/MM/yyyy` for vi, `MM/dd/yyyy` for en. */
  formatDate(value: Instant, options: Intl.DateTimeFormatOptions = DATE_OPTIONS): string {
    return this.dateFormat(this.tag(), options).format(toDate(value));
  }

  /** The caller's options; medium date + short time by default. */
  formatDateTime(value: Instant, options: Intl.DateTimeFormatOptions = DATE_TIME_OPTIONS): string {
    return this.dateFormat(this.tag(), options).format(toDate(value));
  }

  formatNumber(value: number, options?: Intl.NumberFormatOptions): string {
    return this.numberFormat(this.tag(), options).format(value);
  }

  /** "3 giờ trước" / "in 3 hours" relative to `options.now`. */
  formatRelative(value: Instant, options: RelativeOptions = {}): string {
    const deltaMs = toDate(value).getTime() - toDate(options.now ?? Date.now()).getTime();
    const [unit, size] = RELATIVE_UNITS.find(([, ms]) => Math.abs(deltaMs) >= ms) ?? (['minute', 60_000] as const);
    const format = this.relativeFormat(this.tag(), options.numeric ?? 'auto');
    return format.format(Math.trunc(deltaMs / size), unit);
  }

  /**
   * A schedule value: the wall-clock time in `timeZoneId` followed by the zone id, so a time is never ambiguous
   * (README D10). `timeZoneId` must be an IANA id; an unknown id throws `RangeError`.
   */
  formatInZone(instant: Instant, timeZoneId: string): string {
    const text = this.dateFormat(this.tag(), { ...ZONED_OPTIONS, timeZone: timeZoneId }).format(toDate(instant));
    return `${text} ${timeZoneId}`;
  }

  private tag(): 'vi-VN' | 'en-US' {
    return LOCALE_TAG[this.localeState()];
  }

  private dateFormat(tag: string, options: Intl.DateTimeFormatOptions): Intl.DateTimeFormat {
    const key = `${tag}|${JSON.stringify(options)}`;
    let format = this.dateFormats.get(key);
    if (format === undefined) {
      format = new Intl.DateTimeFormat(tag, options);
      this.dateFormats.set(key, format);
    }
    return format;
  }

  private numberFormat(tag: string, options: Intl.NumberFormatOptions | undefined): Intl.NumberFormat {
    const key = `${tag}|${JSON.stringify(options ?? {})}`;
    let format = this.numberFormats.get(key);
    if (format === undefined) {
      format = new Intl.NumberFormat(tag, options);
      this.numberFormats.set(key, format);
    }
    return format;
  }

  private relativeFormat(tag: string, numeric: Intl.RelativeTimeFormatNumeric): Intl.RelativeTimeFormat {
    const key = `${tag}|${numeric}`;
    let format = this.relativeFormats.get(key);
    if (format === undefined) {
      format = new Intl.RelativeTimeFormat(tag, { numeric });
      this.relativeFormats.set(key, format);
    }
    return format;
  }
}

function toDate(value: Instant): Date {
  return value instanceof Date ? value : new Date(value);
}
