import { readFileSync } from 'node:fs';
import { join } from 'node:path';
import { TestBed } from '@angular/core/testing';
import { By } from '@angular/platform-browser';
import { TranslocoTestingModule } from '@jsverse/transloco';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { type AppError, AppErrorMapper } from '@core/errors';
import errorsEn from '../../../../assets/i18n/errors/en.json';
import { FormErrorAlert } from './form-error-alert.component';
import { RetryCountdown } from '../retry-countdown/retry-countdown.component';
import { onEmit } from '../testing/on-emit';

const translations = TranslocoTestingModule.forRoot({
  langs: {
    en: {},
    'errors/en': errorsEn,
  },
  translocoConfig: { availableLangs: ['en'], defaultLang: 'en', fallbackLang: 'en' },
  preloadLangs: true,
});

const mapper = {
  messageKey(error: AppError): string {
    return error.code === 'validation_failed' ? 'errors.validation_failed' : 'errors.unknown_error';
  },
  fieldMessageKey(error: AppError, pointer: string): string {
    return error.fieldErrors[pointer]?.[0] ?? 'errors.unknown_error';
  },
  traceFooter(error: AppError): { key: 'errors.unknown.trace'; params: { traceId: string } } | null {
    return error.traceId === undefined
      ? null
      : { key: 'errors.unknown.trace', params: { traceId: error.traceId } };
  },
} satisfies Pick<AppErrorMapper, 'messageKey' | 'fieldMessageKey' | 'traceFooter'>;

const validationError: AppError = {
  code: 'validation_failed',
  status: 400,
  kind: 'validation',
  fieldErrors: { missingControl: ['errors.validation_failed'] },
};

const configure = async () => {
  await TestBed.configureTestingModule({
    imports: [FormErrorAlert, translations],
    providers: [{ provide: AppErrorMapper, useValue: mapper }],
  }).compileComponents();
};

afterEach(() => {
  vi.useRealTimers();
});

describe('FormErrorAlert', () => {
  it('FormErrorAlert_RendersUnmappedSummary', async () => {
    await configure();
    const fixture = TestBed.createComponent(FormErrorAlert);
    fixture.componentRef.setInput('error', validationError);
    fixture.componentRef.setInput('unmappedKeys', ['missingControl']);
    fixture.detectChanges();
    await fixture.whenStable();

    const root = fixture.nativeElement as HTMLElement;
    expect(root.getAttribute('role')).toBe('alert');
    expect(root.id).toBe(fixture.componentInstance.alertId);
    expect(root.tabIndex).toBe(-1);
    expect(root.textContent).toContain(errorsEn.validation_failed);
    expect(root.textContent).toContain(errorsEn.form.unmappedCount.replace('{{count}}', '1'));
    expect(root.querySelectorAll('li')).toHaveLength(0);
  });

  it('FormErrorAlert_5000UnmappedPointers_RendersAtMostTenDistinctNonTitleRows', async () => {
    await configure();
    const fixture = TestBed.createComponent(FormErrorAlert);
    const messageKeys = [
      'errors.validation_failed',
      'errors.voice_language_mismatch',
      'errors.story_invalid_content',
      'errors.fact_sheet_invalid_content',
      'errors.bible_invalid_content',
      'errors.source_url_invalid',
      'errors.source_domain_not_allowed',
      'errors.autopilot_settings_invalid',
      'errors.story_attestation_required',
      'errors.idempotency_key_required',
      'errors.unauthorized',
      'errors.session_revoked',
      'errors.refresh_token_reused',
    ] as const;
    const pointers = Array.from({ length: 5_000 }, (_, index) => `missing.${index}`);
    const fieldErrors = Object.fromEntries(
      pointers.map((pointer, index) => [pointer, [messageKeys[index % messageKeys.length]!]]),
    );
    fixture.componentRef.setInput('error', { ...validationError, fieldErrors });
    fixture.componentRef.setInput('unmappedKeys', pointers);
    fixture.detectChanges();
    await fixture.whenStable();

    const root = fixture.nativeElement as HTMLElement;
    const rows = Array.from(root.querySelectorAll('li'), (row) => row.textContent?.trim() ?? '');
    expect(root.textContent).toContain(errorsEn.form.unmappedCount.replace('{{count}}', '5000'));
    expect(rows).toHaveLength(10);
    expect(new Set(rows).size).toBe(rows.length);
    expect(rows).not.toContain(errorsEn.validation_failed);
  });

  it('renders nothing for a null error and hides the trace when requested', async () => {
    await configure();
    const fixture = TestBed.createComponent(FormErrorAlert);
    fixture.detectChanges();
    await fixture.whenStable();
    const root = fixture.nativeElement as HTMLElement;
    expect(root.textContent?.trim()).toBe('');
    expect(root.hasAttribute('role')).toBe(false);
    expect(root.hasAttribute('tabindex')).toBe(false);

    fixture.componentRef.setInput('error', { ...validationError, traceId: 'trace-hidden' });
    fixture.componentRef.setInput('showTrace', false);
    fixture.detectChanges();
    await fixture.whenStable();
    expect(root.textContent).not.toContain('trace-hidden');
  });

  it('starts the inline countdown from retryAfterSec', async () => {
    await configure();
    const fixture = TestBed.createComponent(FormErrorAlert);
    fixture.componentRef.setInput('error', { ...validationError, retryAfterSec: 30 });
    fixture.detectChanges();
    await fixture.whenStable();
    const countdown = fixture.debugElement.query(By.directive(RetryCountdown)).injector.get(RetryCountdown);
    expect(countdown.remaining()).toBe(30);
    fixture.destroy();
  });

  it('FormErrorAlert_EqualRetryAfterOnANewError_RestartsAndElapses', async () => {
    vi.useFakeTimers();
    vi.setSystemTime(0);
    await configure();
    const fixture = TestBed.createComponent(FormErrorAlert);
    fixture.componentRef.setInput('error', { ...validationError, retryAfterSec: 30 });
    fixture.detectChanges();
    await fixture.whenStable();
    const firstCountdown = fixture.debugElement.query(By.directive(RetryCountdown)).injector.get(RetryCountdown);

    vi.advanceTimersByTime(10_000);
    expect(firstCountdown.remaining()).toBe(20);

    fixture.componentRef.setInput('error', { ...validationError, retryAfterSec: 30 });
    fixture.detectChanges();
    const replacementCountdown = fixture.debugElement.query(By.directive(RetryCountdown)).injector.get(RetryCountdown);
    const elapsed = vi.fn();
    onEmit(replacementCountdown.elapsed, elapsed);
    expect(replacementCountdown).not.toBe(firstCountdown);
    expect(replacementCountdown.remaining()).toBe(30);
    expect(vi.getTimerCount()).toBe(1);

    vi.advanceTimersByTime(30_000);
    expect(elapsed).toHaveBeenCalledTimes(1);
    expect(vi.getTimerCount()).toBe(0);
    fixture.destroy();
  });

  it('renders only translated alert text plus the mapped trace footer', async () => {
    await configure();
    const fixture = TestBed.createComponent(FormErrorAlert);
    const traceId = 'trace-'.repeat(80);
    const error: AppError = {
      code: 'unknown_error',
      status: 500,
      kind: 'unknown',
      fieldErrors: {},
      traceId,
    };
    fixture.componentRef.setInput('error', error);
    fixture.detectChanges();
    await fixture.whenStable();

    const root = fixture.nativeElement as HTMLElement;
    const text = root.textContent?.replace(/\s+/g, ' ').trim();
    expect(text).toBe(`${errorsEn.unknown_error} ${errorsEn.unknown.trace.replace('{{traceId}}', traceId)}`);
    expect(root.querySelector('.form-error-alert__trace')?.getAttribute('title')).toBe(traceId);
    const styles = readFileSync(join(process.cwd(), 'src/app/shared/ui/form-error-alert/form-error-alert.component.scss'), 'utf8');
    expect(styles).toContain('overflow-wrap: anywhere');
    expect(styles).toContain('text-overflow: ellipsis');
  });

  it('FormErrorAlert_RetryAfterZero_ShowsRetryNow_AndClearingTheErrorRemovesTheAlert', async () => {
    await configure();
    const fixture = TestBed.createComponent(FormErrorAlert);
    fixture.componentRef.setInput('error', { ...validationError, retryAfterSec: 0 });
    fixture.detectChanges();
    await fixture.whenStable();
    const root = fixture.nativeElement as HTMLElement;
    expect(root.textContent).toContain(errorsEn.retryNow);
    expect(root.getAttribute('role')).toBe('alert');

    fixture.componentRef.setInput('error', null);
    fixture.detectChanges();
    await fixture.whenStable();
    expect(root.textContent?.trim()).toBe('');
    expect(root.hasAttribute('role')).toBe(false);
    expect(root.querySelector('nts-retry-countdown')).toBeNull();
  });

  it('FormErrorAlert_HostileTextInErrorFields_IsRenderedAsTextNeverAsMarkup', async () => {
    await configure();
    const fixture = TestBed.createComponent(FormErrorAlert);
    const payload = '<img src=x onerror="window.__qaXss=1"><script>window.__qaXss=2</script>';
    const error: AppError = {
      code: 'unknown_error',
      status: 500,
      kind: 'unknown',
      fieldErrors: { [payload]: [payload] },
      traceId: payload,
      problem: { code: 'x', title: payload, detail: payload, type: payload },
    };
    fixture.componentRef.setInput('error', error);
    fixture.componentRef.setInput('unmappedKeys', [payload]);
    fixture.detectChanges();
    await fixture.whenStable();
    const root = fixture.nativeElement as HTMLElement;
    expect(root.querySelector('img, script')).toBeNull();
    expect((window as unknown as Record<string, unknown>)['__qaXss']).toBeUndefined();
    // only the trace id (a server-generated opaque id) is echoed, as text
    expect(root.querySelector('.form-error-alert__trace')?.textContent).toContain(payload);
  });

  it('keeps every new component template free of innerHTML bindings', () => {
    const root = join(process.cwd(), 'src/app/shared/ui');
    const templates = [
      'form-error-alert/form-error-alert.component.html',
      'retry-countdown/retry-countdown.component.html',
      'conflict-dialog/conflict-dialog.component.html',
    ];
    for (const template of templates) {
      expect(readFileSync(join(root, template), 'utf8')).not.toMatch(/\[innerHTML\]/i);
    }
  });
});
