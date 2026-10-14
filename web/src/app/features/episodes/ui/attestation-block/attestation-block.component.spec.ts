import { TestBed } from '@angular/core/testing';
import { TranslocoService, TranslocoTestingModule } from '@jsverse/transloco';
import { afterEach, describe, expect, it } from 'vitest';
import { onEmit } from '@shared/ui/testing/on-emit';
import { ZONELESS_TEST_PROVIDERS } from '../../../../testing/providers';
import coreEn from '../../../../../assets/i18n/core/en.json';
import coreVi from '../../../../../assets/i18n/core/vi.json';
import episodesEn from '../../../../../assets/i18n/episodes/en.json';
import episodesVi from '../../../../../assets/i18n/episodes/vi.json';
import errorsEn from '../../../../../assets/i18n/errors/en.json';
import errorsVi from '../../../../../assets/i18n/errors/vi.json';
import type { Attestation } from '../../domain/story.model';
import { AttestationBlockComponent } from './attestation-block.component';

const statement: Attestation = { required: true, textVersion: 'v1', text: 'First paragraph.\n\nSecond paragraph.', acceptedAt: null };

async function setup(attestation: Attestation = statement, lang: 'vi' | 'en' = 'vi') {
  await TestBed.configureTestingModule({
    imports: [AttestationBlockComponent, TranslocoTestingModule.forRoot({
      langs: { en: { ...coreEn, episodes: episodesEn, errors: errorsEn }, vi: { ...coreVi, episodes: episodesVi, errors: errorsVi }, 'episodes/en': episodesEn, 'episodes/vi': episodesVi, 'errors/en': errorsEn, 'errors/vi': errorsVi },
      translocoConfig: { availableLangs: ['vi', 'en'], defaultLang: lang, fallbackLang: lang, reRenderOnLangChange: true },
      preloadLangs: true,
    })],
    providers: [...ZONELESS_TEST_PROVIDERS],
  }).compileComponents();
  const fixture = TestBed.createComponent(AttestationBlockComponent);
  fixture.componentRef.setInput('attestation', attestation);
  await fixture.whenStable();
  return { fixture, component: fixture.componentInstance, root: fixture.nativeElement as HTMLElement };
}

afterEach(() => TestBed.resetTestingModule());

describe('AttestationBlockComponent', () => {
  it.each(['vi', 'en'] as const)('groups the statement in a fieldset with one paragraph per block in %s', async (lang) => {
    const { root } = await setup(statement, lang);
    expect(root.querySelector('fieldset legend')?.textContent?.trim()).toBe(TestBed.inject(TranslocoService).translate('episodes.approve.attestation.legend'));
    expect([...root.querySelectorAll('.attestation__text')].map((paragraph) => paragraph.textContent)).toEqual(['First paragraph.', 'Second paragraph.']);
  });

  it('shows markup literally without creating markup elements', async () => {
    const text = '<b>x</b><script>alert(1)</script>';
    const { root } = await setup({ ...statement, text });
    expect(root.querySelector('.attestation__text')?.textContent).toBe(text);
    expect(root.querySelector('b, script')).toBeNull();
  });

  it('links the native checkbox to its error and clears invalid state with the host input', async () => {
    const { fixture, root } = await setup();
    fixture.componentRef.setInput('invalid', true);
    await fixture.whenStable();
    const checkbox = root.querySelector<HTMLInputElement>('input[type="checkbox"]')!;
    const error = root.querySelector<HTMLElement>('.attestation__error')!;
    expect(checkbox.getAttribute('aria-invalid')).toBe('true');
    expect(checkbox.getAttribute('aria-describedby')).toBe(error.id);
    expect(error.textContent?.trim()).toBe(TestBed.inject(TranslocoService).translate('errors.story_attestation_required'));
    fixture.componentRef.setInput('invalid', false);
    await fixture.whenStable();
    expect(checkbox.getAttribute('aria-invalid')).toBe('false');
    expect(checkbox.getAttribute('aria-describedby') ?? '').toBe('');
    expect(root.querySelector('.attestation__error')).toBeNull();
  });

  it('emits the tick intent and lets the host control the accepted value', async () => {
    const { fixture, component, root } = await setup();
    const ticks: boolean[] = [];
    onEmit(component.acceptedChange, (value) => ticks.push(value));
    root.querySelector<HTMLInputElement>('input[type="checkbox"]')!.click();
    await fixture.whenStable();
    expect(ticks).toEqual([true]);
    fixture.componentRef.setInput('accepted', true);
    await fixture.whenStable();
    expect(root.querySelector<HTMLInputElement>('input[type="checkbox"]')!.checked).toBe(true);
  });

  it('keeps a missing statement empty and the checkbox safely disabled', async () => {
    const { root } = await setup({ ...statement, text: null });
    expect(root.querySelector('.attestation__text')).toBeNull();
    expect(root.querySelector<HTMLInputElement>('input[type="checkbox"]')!.disabled).toBe(true);
  });
});
