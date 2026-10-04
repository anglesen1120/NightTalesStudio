import { TestBed } from '@angular/core/testing';
import { describe, expect, it } from 'vitest';
import { uiTestingTranslations } from '../testing/ui-testing-translations';
import { StatusChip } from './status-chip';

describe('StatusChip', () => {
  it('pairs visible translated text with the accessible name', async () => {
    await TestBed.configureTestingModule({ imports: [StatusChip, uiTestingTranslations()] }).compileComponents();
    const fixture = TestBed.createComponent(StatusChip);
    fixture.componentRef.setInput('statusKey', 'published');
    fixture.componentRef.setInput('appearance', 'success');
    fixture.detectChanges();
    await fixture.whenStable();
    const root = fixture.nativeElement as HTMLElement;
    const text = (root.textContent ?? '').trim();
    expect(text).toBe('Đã xuất bản');
    expect(root.getAttribute('aria-label')).toBe(text);
    expect(root.querySelector('svg')).not.toBeNull();
  });

  it('falls back unknown wire codes to a neutral icon-and-text state', async () => {
    await TestBed.configureTestingModule({ imports: [StatusChip, uiTestingTranslations()] }).compileComponents();
    const fixture = TestBed.createComponent(StatusChip);
    fixture.componentRef.setInput('statusKey', 'future_backend_state');
    fixture.componentRef.setInput('appearance', 'error');
    fixture.detectChanges();
    await fixture.whenStable();
    const root = fixture.nativeElement as HTMLElement;
    expect(root.textContent).toContain('Trạng thái chưa xác định');
    expect(root.classList.contains('status-chip--neutral')).toBe(true);
    expect(root.querySelector('nts-icon-unknown')).not.toBeNull();
  });

  for (const appearance of ['info', 'success', 'warn', 'error'] as const) {
    it(`keeps unknown codes neutral even when ${appearance} is requested`, async () => {
      await TestBed.configureTestingModule({ imports: [StatusChip, uiTestingTranslations()] }).compileComponents();
      const fixture = TestBed.createComponent(StatusChip);
      fixture.componentRef.setInput('statusKey', 'brand_new_code');
      fixture.componentRef.setInput('appearance', appearance);
      fixture.detectChanges();
      await fixture.whenStable();
      const root = fixture.nativeElement as HTMLElement;
      expect(root.classList.contains('status-chip--neutral')).toBe(true);
      expect(root.classList.contains(`status-chip--${appearance}`)).toBe(false);
      expect(root.getAttribute('aria-label')).toBe('Trạng thái chưa xác định');
    });
  }

  for (const key of ['constructor', 'toString', '__proto__', 'hasOwnProperty']) {
    it(`treats the prototype key "${key}" as an unknown status`, async () => {
      await TestBed.configureTestingModule({ imports: [StatusChip, uiTestingTranslations()] }).compileComponents();
      const fixture = TestBed.createComponent(StatusChip);
      fixture.componentRef.setInput('statusKey', key);
      fixture.componentRef.setInput('appearance', 'success');
      fixture.detectChanges();
      await fixture.whenStable();
      const root = fixture.nativeElement as HTMLElement;
      expect(root.classList.contains('status-chip--neutral')).toBe(true);
      expect(root.classList.contains('status-chip--success')).toBe(false);
      expect(root.getAttribute('aria-label')).toBe('Trạng thái chưa xác định');
      expect(root.textContent).toContain('Trạng thái chưa xác định');
      expect(root.querySelector('nts-icon-unknown')).not.toBeNull();
    });
  }
});
