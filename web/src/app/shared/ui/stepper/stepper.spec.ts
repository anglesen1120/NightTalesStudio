import { TestBed } from '@angular/core/testing';
import { describe, expect, it } from 'vitest';
import { uiTestingTranslations } from '../testing/ui-testing-translations';
import { Stepper } from './stepper';

describe('Stepper', () => {
  it('uses ordered-list semantics and announces current and done states', async () => {
    await TestBed.configureTestingModule({ imports: [Stepper, uiTestingTranslations()] }).compileComponents();
    const fixture = TestBed.createComponent(Stepper);
    fixture.componentRef.setInput('steps', [
      { labelKey: 'core.ui.theme.dark', state: 'done' },
      { labelKey: 'core.ui.theme.light', state: 'current' },
      { labelKey: 'core.ui.confirm.confirm', state: 'todo' },
    ]);
    fixture.detectChanges();
    await fixture.whenStable();
    const root = fixture.nativeElement as HTMLElement;
    expect(root.querySelector('ol')?.getAttribute('role')).toBe('list');
    expect(root.querySelectorAll('li')).toHaveLength(3);
    expect(root.querySelector('[aria-current="step"]')?.textContent).toContain('Sáng');
    expect(root.querySelector('.stepper__step--done')?.textContent).toContain('đã xong');
  });
});
