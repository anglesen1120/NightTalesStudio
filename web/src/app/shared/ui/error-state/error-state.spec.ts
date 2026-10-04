import { TestBed } from '@angular/core/testing';
import { describe, expect, it, vi } from 'vitest';
import { uiTestingTranslations } from '../testing/ui-testing-translations';
import { ErrorState } from './error-state';

describe('ErrorState', () => {
  it('renders mapped copy, trace id and retry without logging content', async () => {
    await TestBed.configureTestingModule({ imports: [ErrorState, uiTestingTranslations()] }).compileComponents();
    const fixture = TestBed.createComponent(ErrorState);
    fixture.componentRef.setInput('bodyKey', 'core.ui.errorState.body');
    fixture.componentRef.setInput('traceId', 'trace-123');
    fixture.componentRef.setInput('retryLabelKey', 'core.ui.errorState.retry');
    const retry = vi.fn();
    const log = vi.spyOn(console, 'log');
    fixture.componentInstance.retry.subscribe(retry);
    fixture.detectChanges();
    await fixture.whenStable();
    expect(fixture.nativeElement.getAttribute('role')).toBe('alert');
    expect(fixture.nativeElement.getAttribute('aria-describedby')).toBe(fixture.nativeElement.querySelector('p')?.id);
    expect(fixture.nativeElement.textContent).toContain('trace-123');
    (fixture.nativeElement.querySelector('button') as HTMLButtonElement).click();
    expect(retry).toHaveBeenCalledOnce();
    expect(log).not.toHaveBeenCalled();
    log.mockRestore();
  });

  it('renders no retry button or trace line when their inputs are absent', async () => {
    await TestBed.configureTestingModule({ imports: [ErrorState, uiTestingTranslations()] }).compileComponents();
    const fixture = TestBed.createComponent(ErrorState);
    fixture.componentRef.setInput('bodyKey', 'core.ui.errorState.body');
    fixture.detectChanges();
    await fixture.whenStable();
    expect(fixture.nativeElement.querySelector('button')).toBeNull();
    expect(fixture.nativeElement.querySelector('.error-state__trace')).toBeNull();
  });

  it('renders a hostile trace id as inert text, never as markup', async () => {
    await TestBed.configureTestingModule({ imports: [ErrorState, uiTestingTranslations()] }).compileComponents();
    const fixture = TestBed.createComponent(ErrorState);
    fixture.componentRef.setInput('bodyKey', 'core.ui.errorState.body');
    fixture.componentRef.setInput('traceId', '<img src=x onerror=alert(1)>');
    fixture.detectChanges();
    await fixture.whenStable();
    expect(fixture.nativeElement.querySelector('img')).toBeNull();
    expect(fixture.nativeElement.querySelector('code')?.textContent).toBe('<img src=x onerror=alert(1)>');
  });
});
