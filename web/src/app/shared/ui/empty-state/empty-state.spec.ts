import { TestBed } from '@angular/core/testing';
import { describe, expect, it, vi } from 'vitest';
import { uiTestingTranslations } from '../testing/ui-testing-translations';
import { EmptyState } from './empty-state';

describe('EmptyState', () => {
  it('renders semantic translated copy and emits the native button action', async () => {
    await TestBed.configureTestingModule({ imports: [EmptyState, uiTestingTranslations()] }).compileComponents();
    const fixture = TestBed.createComponent(EmptyState);
    fixture.componentRef.setInput('titleKey', 'core.ui.emptyState.title');
    fixture.componentRef.setInput('bodyKey', 'core.ui.emptyState.body');
    fixture.componentRef.setInput('actionLabelKey', 'core.ui.confirm.confirm');
    const action = vi.fn();
    fixture.componentInstance.action.subscribe(action);
    fixture.detectChanges();
    await fixture.whenStable();
    expect(fixture.nativeElement.getAttribute('role')).toBe('status');
    expect(fixture.nativeElement.textContent).toContain('Chưa có nội dung');
    (fixture.nativeElement.querySelector('button') as HTMLButtonElement).click();
    expect(action).toHaveBeenCalledOnce();
  });

  it('renders no body and no action button when those inputs are absent', async () => {
    await TestBed.configureTestingModule({ imports: [EmptyState, uiTestingTranslations()] }).compileComponents();
    const fixture = TestBed.createComponent(EmptyState);
    fixture.componentRef.setInput('titleKey', 'core.ui.emptyState.title');
    fixture.detectChanges();
    await fixture.whenStable();
    expect(fixture.nativeElement.querySelector('button')).toBeNull();
    expect(fixture.nativeElement.querySelector('p')).toBeNull();
    expect(fixture.nativeElement.querySelectorAll('h2')).toHaveLength(1);
  });
});
