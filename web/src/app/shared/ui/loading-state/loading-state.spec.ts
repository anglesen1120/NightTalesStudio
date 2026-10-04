import { TestBed } from '@angular/core/testing';
import { describe, expect, it } from 'vitest';
import { uiTestingTranslations } from '../testing/ui-testing-translations';
import { LoadingState } from './loading-state';

describe('LoadingState', () => {
  it('clamps rows and exposes a polite busy label', async () => {
    await TestBed.configureTestingModule({ imports: [LoadingState, uiTestingTranslations()] }).compileComponents();
    const fixture = TestBed.createComponent(LoadingState);
    fixture.componentRef.setInput('rows', 9);
    fixture.detectChanges();
    await fixture.whenStable();
    const root = fixture.nativeElement as HTMLElement;
    expect(root.getAttribute('aria-busy')).toBe('true');
    expect(root.querySelector('[aria-live="polite"]')?.textContent).toContain('Đang tải');
    expect(root.querySelector('[role="status"]')?.textContent).toContain('Đang tải');
    expect(root.querySelectorAll('.loading-state__skeleton')).toHaveLength(6);
    expect(root.querySelector('[aria-hidden="true"]')).not.toBeNull();
  });

  for (const rows of [0, -4, 1]) {
    it(`never renders fewer than three skeleton rows for rows=${rows}`, async () => {
      await TestBed.configureTestingModule({ imports: [LoadingState, uiTestingTranslations()] }).compileComponents();
      const fixture = TestBed.createComponent(LoadingState);
      fixture.componentRef.setInput('rows', rows);
      fixture.detectChanges();
      await fixture.whenStable();
      expect((fixture.nativeElement as HTMLElement).querySelectorAll('.loading-state__skeleton')).toHaveLength(3);
    });
  }

  for (const rows of [NaN, Infinity, -Infinity]) {
    it(`falls back to the default three skeleton rows for non-finite rows=${rows}`, async () => {
      await TestBed.configureTestingModule({ imports: [LoadingState, uiTestingTranslations()] }).compileComponents();
      const fixture = TestBed.createComponent(LoadingState);
      fixture.componentRef.setInput('rows', rows);
      fixture.detectChanges();
      await fixture.whenStable();
      expect((fixture.nativeElement as HTMLElement).querySelectorAll('.loading-state__skeleton')).toHaveLength(3);
    });
  }
});
