import { ComponentFixture, TestBed } from '@angular/core/testing';
import { describe, expect, it, vi } from 'vitest';
import { uiTestingTranslations } from '../testing/ui-testing-translations';
import { CopyableCode } from './copyable-code';

const liveText = (fixture: ComponentFixture<CopyableCode>): string =>
  (fixture.nativeElement.querySelector('[aria-live="polite"]').textContent as string).trim();

const waitForLive = (fixture: ComponentFixture<CopyableCode>, expected: string): Promise<void> =>
  vi.waitFor(() => {
    fixture.detectChanges();
    expect(liveText(fixture)).toContain(expected);
  });

describe('CopyableCode', () => {
  it('copies and announces success politely', async () => {
    const writeText = vi.fn().mockResolvedValue(undefined);
    Object.defineProperty(navigator, 'clipboard', { configurable: true, value: { writeText } });
    await TestBed.configureTestingModule({ imports: [CopyableCode, uiTestingTranslations()] }).compileComponents();
    const fixture = TestBed.createComponent(CopyableCode);
    fixture.componentRef.setInput('value', 'RECOVERY-01');
    fixture.detectChanges();
    await fixture.whenStable();
    const button = fixture.nativeElement.querySelector('button') as HTMLButtonElement;
    expect(button.getAttribute('aria-label')).toBe('Sao chép mã');
    expect(fixture.nativeElement.querySelector('code').getAttribute('tabindex')).toBe('0');
    button.click();
    expect(writeText).toHaveBeenCalledWith('RECOVERY-01');
    await waitForLive(fixture, 'Đã sao chép');
  });

  it('announces a useful fallback when clipboard is unavailable', async () => {
    Object.defineProperty(navigator, 'clipboard', { configurable: true, value: undefined });
    await TestBed.configureTestingModule({ imports: [CopyableCode, uiTestingTranslations()] }).compileComponents();
    const fixture = TestBed.createComponent(CopyableCode);
    fixture.componentRef.setInput('value', 'RECOVERY-02');
    fixture.detectChanges();
    await fixture.whenStable();
    (fixture.nativeElement.querySelector('button') as HTMLButtonElement).click();
    await waitForLive(fixture, 'Không thể sao chép');
  });

  it('announces the fallback when writeText rejects and shows no result before any click', async () => {
    const writeText = vi.fn().mockRejectedValue(new Error('denied'));
    Object.defineProperty(navigator, 'clipboard', { configurable: true, value: { writeText } });
    await TestBed.configureTestingModule({ imports: [CopyableCode, uiTestingTranslations()] }).compileComponents();
    const fixture = TestBed.createComponent(CopyableCode);
    fixture.componentRef.setInput('value', 'RECOVERY-03');
    fixture.detectChanges();
    await fixture.whenStable();
    expect(liveText(fixture)).toBe('');
    (fixture.nativeElement.querySelector('button') as HTMLButtonElement).click();
    await waitForLive(fixture, 'Không thể sao chép');
    expect(writeText).toHaveBeenCalledWith('RECOVERY-03');
    expect(liveText(fixture)).not.toContain('Đã sao chép');
  });

  it('clears the previous result when the value changes', async () => {
    const writeText = vi.fn().mockResolvedValue(undefined);
    Object.defineProperty(navigator, 'clipboard', { configurable: true, value: { writeText } });
    await TestBed.configureTestingModule({ imports: [CopyableCode, uiTestingTranslations()] }).compileComponents();
    const fixture = TestBed.createComponent(CopyableCode);
    fixture.componentRef.setInput('value', 'RECOVERY-04');
    fixture.detectChanges();
    await fixture.whenStable();
    (fixture.nativeElement.querySelector('button') as HTMLButtonElement).click();
    await waitForLive(fixture, 'Đã sao chép');
    fixture.componentRef.setInput('value', 'RECOVERY-05');
    fixture.detectChanges();
    await fixture.whenStable();
    expect(liveText(fixture)).toBe('');
  });

  it('re-announces a repeated copy by clearing the live region before restoring the message', async () => {
    const writeText = vi.fn().mockResolvedValue(undefined);
    Object.defineProperty(navigator, 'clipboard', { configurable: true, value: { writeText } });
    await TestBed.configureTestingModule({ imports: [CopyableCode, uiTestingTranslations()] }).compileComponents();
    const fixture = TestBed.createComponent(CopyableCode);
    fixture.componentRef.setInput('value', 'RECOVERY-06');
    fixture.detectChanges();
    await fixture.whenStable();
    const button = fixture.nativeElement.querySelector('button') as HTMLButtonElement;
    button.click();
    await waitForLive(fixture, 'Đã sao chép');

    const snapshots: string[] = [];
    const observer = new MutationObserver(() => snapshots.push(liveText(fixture)));
    observer.observe(fixture.nativeElement.querySelector('[aria-live="polite"]'), {
      childList: true,
      characterData: true,
      subtree: true,
    });
    button.click();
    await vi.waitFor(() => {
      fixture.detectChanges();
      expect(snapshots).toContain('');
      expect(snapshots.at(-1)).toContain('Đã sao chép');
    });
    observer.disconnect();
    expect(snapshots.indexOf('')).toBeLessThan(snapshots.length - 1);
    expect(writeText).toHaveBeenCalledTimes(2);
  });
});
