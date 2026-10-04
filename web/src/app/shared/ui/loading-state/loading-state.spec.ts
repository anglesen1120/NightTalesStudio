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
    expect(fixture.nativeElement.getAttribute('aria-busy')).toBe('true');
    expect(fixture.nativeElement.querySelector('[aria-live="polite"]')?.textContent).toContain('Đang tải');
    expect(fixture.nativeElement.querySelectorAll('.loading-state__skeleton')).toHaveLength(6);
    expect(fixture.nativeElement.querySelector('[aria-hidden="true"]')).not.toBeNull();
  });

  for (const rows of [0, -4, 1]) {
    it(`never renders fewer than three skeleton rows for rows=${rows}`, async () => {
      await TestBed.configureTestingModule({ imports: [LoadingState, uiTestingTranslations()] }).compileComponents();
      const fixture = TestBed.createComponent(LoadingState);
      fixture.componentRef.setInput('rows', rows);
      fixture.detectChanges();
      await fixture.whenStable();
      expect(fixture.nativeElement.querySelectorAll('.loading-state__skeleton')).toHaveLength(3);
    });
  }
});
