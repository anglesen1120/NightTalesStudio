import { TestBed } from '@angular/core/testing';
import { TranslocoTestingModule } from '@jsverse/transloco';
import { afterEach, describe, expect, it, vi } from 'vitest';
import errorsEn from '../../../../assets/i18n/errors/en.json';
import { onEmit } from '../testing/on-emit';
import { RetryCountdown } from './retry-countdown.component';

const translations = TranslocoTestingModule.forRoot({
  langs: { en: {}, 'errors/en': errorsEn },
  translocoConfig: { availableLangs: ['en'], defaultLang: 'en', fallbackLang: 'en' },
  preloadLangs: true,
});

afterEach(() => {
  vi.useRealTimers();
});

describe('RetryCountdown', () => {
  it('RetryCountdown_TicksToZero_AndCleansUp', async () => {
    vi.useFakeTimers();
    await TestBed.configureTestingModule({ imports: [RetryCountdown, translations] }).compileComponents();
    const fixture = TestBed.createComponent(RetryCountdown);
    const elapsed = vi.fn();
    onEmit(fixture.componentInstance.elapsed, elapsed);
    fixture.componentRef.setInput('seconds', 2);
    fixture.detectChanges();
    await fixture.whenStable();

    const root = fixture.nativeElement as HTMLElement;
    expect(fixture.componentInstance.remaining()).toBe(2);
    expect(root.querySelector('.retry-countdown__announcement')?.textContent).toBe(errorsEn.retryIn.replace('{{seconds}}', '2'));
    expect(vi.getTimerCount()).toBe(1);

    vi.advanceTimersByTime(1_000);
    fixture.detectChanges();
    expect(fixture.componentInstance.remaining()).toBe(1);
    expect(root.querySelector('.retry-countdown__announcement')?.textContent).toBe(errorsEn.retryIn.replace('{{seconds}}', '2'));
    expect(elapsed).not.toHaveBeenCalled();

    vi.advanceTimersByTime(1_000);
    fixture.detectChanges();
    expect(fixture.componentInstance.remaining()).toBe(0);
    expect(root.querySelector('.retry-countdown__announcement')?.textContent).toBe(errorsEn.retryNow);
    expect(elapsed).toHaveBeenCalledTimes(1);
    expect(vi.getTimerCount()).toBe(0);

    fixture.destroy();
    vi.advanceTimersByTime(5_000);
    expect(elapsed).toHaveBeenCalledTimes(1);
  });

  it('RetryCountdown_RequiredInput_Rearm_AndDestroy', async () => {
    vi.useFakeTimers();
    await TestBed.configureTestingModule({ imports: [RetryCountdown, translations] }).compileComponents();
    const fixture = TestBed.createComponent(RetryCountdown);
    const elapsed = vi.fn();
    onEmit(fixture.componentInstance.elapsed, elapsed);

    fixture.componentRef.setInput('seconds', 30);
    expect(() => fixture.detectChanges()).not.toThrow();
    expect(fixture.componentInstance.remaining()).toBe(30);
    expect(vi.getTimerCount()).toBe(1);

    fixture.componentRef.setInput('seconds', 2);
    fixture.detectChanges();
    expect(fixture.componentInstance.remaining()).toBe(2);
    expect(vi.getTimerCount()).toBe(1);
    vi.advanceTimersByTime(2_000);
    expect(elapsed).toHaveBeenCalledTimes(1);
    expect(vi.getTimerCount()).toBe(0);

    for (const [seconds, count] of [[0, 2], [-4, 3], [Number.NaN, 4]] as const) {
      fixture.componentRef.setInput('seconds', seconds);
      fixture.detectChanges();
      expect(fixture.componentInstance.remaining()).toBe(0);
      expect(elapsed).toHaveBeenCalledTimes(count);
      expect(vi.getTimerCount()).toBe(0);
    }

    fixture.componentRef.setInput('seconds', 5);
    fixture.detectChanges();
    expect(vi.getTimerCount()).toBe(1);
    fixture.destroy();
    expect(vi.getTimerCount()).toBe(0);
    vi.advanceTimersByTime(5_000);
    expect(elapsed).toHaveBeenCalledTimes(4);
  });
});
