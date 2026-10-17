import { TestBed } from '@angular/core/testing';
import { TranslocoTestingModule } from '@jsverse/transloco';
import { afterEach, describe, expect, it } from 'vitest';
import { ZONELESS_TEST_PROVIDERS } from '../../../testing/providers';
import storyEn from '../../../../assets/i18n/story/en.json';
import storyVi from '../../../../assets/i18n/story/vi.json';
import { RevisedBadgeComponent } from './revised-badge.component';

afterEach(() => TestBed.resetTestingModule());

describe('RevisedBadgeComponent', () => {
  it.each([
    ['vi', storyVi, 'Đã tự sửa một lần'],
    ['en', storyEn, 'Automatically corrected once'],
  ] as const)('renders the exact shared revised badge in %s without a link', async (language, copy, expected) => {
    await TestBed.configureTestingModule({
      imports: [RevisedBadgeComponent, TranslocoTestingModule.forRoot({
        langs: { [language]: {}, [`story/${language}`]: copy },
        translocoConfig: { availableLangs: [language], defaultLang: language, fallbackLang: language },
        preloadLangs: true,
      })],
      providers: ZONELESS_TEST_PROVIDERS,
    }).compileComponents();
    const fixture = TestBed.createComponent(RevisedBadgeComponent);
    fixture.componentRef.setInput('describedById', 'audit-drift');
    await fixture.whenStable();
    const root = fixture.nativeElement as HTMLElement;
    expect(root.textContent?.trim()).toBe(expected);
    expect(root.textContent?.trim()).toBe(copy.generation.revised.badge);
    expect(root.querySelector('a, button')).toBeNull();
    expect(root.querySelector('.revised-badge')?.getAttribute('aria-describedby')).toBe('audit-drift');
    fixture.componentRef.setInput('describedById', null);
    await fixture.whenStable();
    expect(root.querySelector('.revised-badge')?.hasAttribute('aria-describedby')).toBe(false);
  });
});
