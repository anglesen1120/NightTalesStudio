import { TestBed } from '@angular/core/testing';
import { describe, expect, it, vi } from 'vitest';
import { uiTestingTranslations } from '../testing/ui-testing-translations';
import { CopyableCode } from './copyable-code';

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
    await fixture.whenStable();
    expect(writeText).toHaveBeenCalledWith('RECOVERY-01');
    expect(fixture.nativeElement.querySelector('[aria-live="polite"]').textContent).toContain('Đã sao chép');
  });

  it('announces a useful fallback when clipboard is unavailable', async () => {
    Object.defineProperty(navigator, 'clipboard', { configurable: true, value: undefined });
    await TestBed.configureTestingModule({ imports: [CopyableCode, uiTestingTranslations()] }).compileComponents();
    const fixture = TestBed.createComponent(CopyableCode);
    fixture.componentRef.setInput('value', 'RECOVERY-02');
    fixture.detectChanges();
    await fixture.whenStable();
    (fixture.nativeElement.querySelector('button') as HTMLButtonElement).click();
    await fixture.whenStable();
    expect(fixture.nativeElement.querySelector('[aria-live="polite"]').textContent).toContain('Không thể sao chép');
  });
});
