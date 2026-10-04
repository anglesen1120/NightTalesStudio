import { TestBed } from '@angular/core/testing';
import { describe, expect, it, vi } from 'vitest';
import { uiTestingTranslations } from '../testing/ui-testing-translations';
import { onEmit } from '../testing/on-emit';
import { EmptyState } from './empty-state';

describe('EmptyState', () => {
  it('renders semantic translated copy and emits the native button action', async () => {
    await TestBed.configureTestingModule({ imports: [EmptyState, uiTestingTranslations()] }).compileComponents();
    const fixture = TestBed.createComponent(EmptyState);
    fixture.componentRef.setInput('titleKey', 'core.ui.emptyState.title');
    fixture.componentRef.setInput('bodyKey', 'core.ui.emptyState.body');
    fixture.componentRef.setInput('actionLabelKey', 'core.ui.confirm.confirm');
    const action = vi.fn();
    onEmit(fixture.componentInstance.action, action);
    fixture.detectChanges();
    await fixture.whenStable();
    const root = fixture.nativeElement as HTMLElement;
    expect(root.getAttribute('role')).toBe('status');
    expect(root.textContent).toContain('Chưa có nội dung');
    root.querySelector<HTMLButtonElement>('button')?.click();
    expect(action).toHaveBeenCalledOnce();
  });

  it('renders no body and no action button when those inputs are absent', async () => {
    await TestBed.configureTestingModule({ imports: [EmptyState, uiTestingTranslations()] }).compileComponents();
    const fixture = TestBed.createComponent(EmptyState);
    fixture.componentRef.setInput('titleKey', 'core.ui.emptyState.title');
    fixture.detectChanges();
    await fixture.whenStable();
    const root = fixture.nativeElement as HTMLElement;
    expect(root.querySelector('button')).toBeNull();
    expect(root.querySelector('p')).toBeNull();
    expect(root.querySelectorAll('h2')).toHaveLength(1);
  });
});
