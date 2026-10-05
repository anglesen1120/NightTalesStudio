import { TestBed } from '@angular/core/testing';
import { FormControl, FormGroup, Validators } from '@angular/forms';
import { provideRouter } from '@angular/router';
import { TranslocoService, TranslocoTestingModule } from '@jsverse/transloco';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { bindFieldErrors, type AppError } from '@core/errors';
import { onEmit } from '@shared/ui/testing/on-emit';
import { ZONELESS_TEST_PROVIDERS } from '../../../testing/providers';
import coreEn from '../../../../assets/i18n/core/en.json';
import coreVi from '../../../../assets/i18n/core/vi.json';
import episodesEn from '../../../../assets/i18n/episodes/en.json';
import episodesVi from '../../../../assets/i18n/episodes/vi.json';
import errorsEn from '../../../../assets/i18n/errors/en.json';
import errorsVi from '../../../../assets/i18n/errors/vi.json';
import storyEn from '../../../../assets/i18n/story/en.json';
import storyVi from '../../../../assets/i18n/story/vi.json';
import type { LengthTier } from '../domain/episode.model';
import { StoryBriefFormComponent } from './story-brief-form.component';

const translations = TranslocoTestingModule.forRoot({
  langs: {
    vi: coreVi, en: coreEn,
    'episodes/vi': episodesVi, 'episodes/en': episodesEn,
    'errors/vi': errorsVi, 'errors/en': errorsEn,
    'story/vi': storyVi, 'story/en': storyEn,
  },
  translocoConfig: { availableLangs: ['vi', 'en'], defaultLang: 'vi', fallbackLang: 'vi', reRenderOnLangChange: true },
  preloadLangs: true,
});

async function setup(brief = 'Một chuyến xe giữa đêm', title: string | null = null) {
  await TestBed.configureTestingModule({
    imports: [StoryBriefFormComponent, translations],
    providers: [...ZONELESS_TEST_PROVIDERS, provideRouter([])],
  }).compileComponents();
  const form = new FormGroup({
    title: new FormControl<string | null>(title, Validators.maxLength(120)),
    brief: new FormControl(brief, { nonNullable: true, validators: [(control) => Validators.required(control), Validators.maxLength(1000)] }),
    lengthTier: new FormControl<LengthTier>('standard', { nonNullable: true }),
  });
  const fixture = TestBed.createComponent(StoryBriefFormComponent);
  fixture.componentRef.setInput('form', form);
  fixture.componentRef.setInput('briefLength', brief.length);
  fixture.componentRef.setInput('canSubmit', form.valid);
  fixture.detectChanges();
  await fixture.whenStable();
  return { fixture, form, root: fixture.nativeElement as HTMLElement, component: fixture.componentInstance };
}

afterEach(() => { TestBed.resetTestingModule(); vi.useRealTimers(); });

describe('StoryBriefFormComponent', () => {
  it('formats the brief counter with the active language and leaves the cost slot empty', async () => {
    const { fixture, root } = await setup('x'.repeat(1000));
    expect(root.querySelector('nts-char-counter')?.textContent).toContain('1.000/1.000');
    TestBed.inject(TranslocoService).setActiveLang('en');
    fixture.detectChanges();
    await fixture.whenStable();
    expect(root.querySelector('nts-char-counter')?.textContent).toContain('1,000/1,000');
    expect(root.querySelector('.brief-form__cost-line')?.textContent).toBe('');
    expect(root.textContent).not.toMatch(/credits|balance|quote|tín dụng|số dư/i);
  });

  it.each([
    ['', null, 'brief', 'briefRequired'],
    ['x'.repeat(1001), null, 'brief', 'briefMax'],
    ['valid', 'x'.repeat(121), 'title', 'titleMax'],
  ] as const)('keeps invalid submission disabled and links its inline validation (%s)', async (brief, title, field, key) => {
    const { root } = await setup(brief, title);
    expect(root.querySelector<HTMLButtonElement>('button[type="submit"]')?.disabled).toBe(true);
    const control = root.querySelector<HTMLElement>(`[data-control="${field}"]`);
    expect(control?.getAttribute('aria-invalid')).toBe('true');
    const errorId = control?.getAttribute('aria-describedby')?.split(' ').find((id) => id.endsWith('-error'));
    expect(errorId).toBeTruthy();
    expect(root.querySelector(`[id="${errorId}"]`)?.textContent).toContain(episodesVi.new.validation[key]);
  });

  it('pairs every editable control with an explicit label and links brief hint and counter', async () => {
    const { root } = await setup();
    for (const control of root.querySelectorAll<HTMLInputElement | HTMLTextAreaElement>('input, textarea')) {
      expect(control.id).not.toBe('');
      expect(root.querySelector(`label[for="${control.id}"]`)).not.toBeNull();
    }
    const brief = root.querySelector('textarea');
    for (const id of brief?.getAttribute('aria-describedby')?.split(' ') ?? []) expect(root.querySelector(`[id="${id}"]`)).not.toBeNull();
    expect(root.querySelectorAll('input[type="radio"]')).toHaveLength(3);
  });

  it('renders bindFieldErrors server message keys as inline text, never raw detail', async () => {
    const { fixture, form, root } = await setup();
    const error: AppError = {
      code: 'validation_failed', status: 400, kind: 'validation',
      fieldErrors: { brief: ['SECRET raw detail'], unknownField: ['SECRET unknown detail'] },
    };
    const unmapped = bindFieldErrors(form, error, { markTouched: true });
    expect(unmapped).toEqual(['unknownField']);
    fixture.componentRef.setInput('canSubmit', false);
    fixture.componentRef.setInput('errorKey', 'errors.validation_failed');
    fixture.componentRef.setInput('unmappedFieldKeys', ['errors.validation_failed']);
    fixture.componentRef.setInput('traceId', 'trace-test');
    fixture.detectChanges();
    await fixture.whenStable();
    const textarea = root.querySelector('textarea');
    expect(textarea?.getAttribute('aria-invalid')).toBe('true');
    const errorId = textarea?.getAttribute('aria-describedby')?.split(' ').find((id) => id.endsWith('-error'));
    expect(root.querySelector(`[id="${errorId}"]`)?.textContent).toContain(errorsVi.validation_failed);
    const alert = root.querySelector('[role="alert"]');
    expect(alert?.textContent).toContain(errorsVi.validation_failed);
    expect(alert?.textContent).toContain('trace-test');
    expect(root.textContent).not.toContain('SECRET');
  });

  it('reacts to repeated INVALID programmatic field errors without another input change', async () => {
    const { fixture, form, root } = await setup();
    fixture.componentRef.setInput('canSubmit', false);
    form.controls.brief.setErrors({ server: { messageKeys: ['errors.validation_failed'] } });
    fixture.detectChanges();
    await fixture.whenStable();
    expect(root.querySelector('.brief-form__error')?.textContent).toContain(errorsVi.validation_failed);
    form.controls.brief.setErrors({ server: { messageKeys: ['errors.network_error'] } });
    await fixture.whenStable();
    expect(root.querySelector('.brief-form__error')?.textContent).toContain(errorsVi.network_error);
  });

  it('emits create once per form submit and uses the true-crime label and note', async () => {
    const { fixture, root, component } = await setup();
    fixture.componentRef.setInput('isTrueCrime', true);
    fixture.detectChanges();
    await fixture.whenStable();
    expect(root.querySelector('button[type="submit"]')?.textContent).toContain(episodesVi.new.trueCrimeNext);
    expect(root.querySelector('[role="note"]')?.textContent).toContain(episodesVi.new.trueCrimeNotice);
    let submitted = 0;
    onEmit(component.submitted, () => submitted++);
    root.querySelector('form')?.dispatchEvent(new Event('submit', { bubbles: true, cancelable: true }));
    expect(submitted).toBe(1);
    fixture.componentRef.setInput('submitting', true);
    fixture.detectChanges();
    await fixture.whenStable();
    expect(root.querySelector<HTMLFieldSetElement>('.brief-form__fields')?.disabled).toBe(true);
    expect(root.querySelector<HTMLButtonElement>('button[type="submit"]')?.disabled).toBe(true);
    root.querySelector('form')?.dispatchEvent(new Event('submit', { bubbles: true, cancelable: true }));
    expect(submitted).toBe(1);
  });

  it('renders the lost-create note and a research gate link without allowing a submit', async () => {
    const { fixture, root, component } = await setup();
    fixture.componentRef.setInput('createLost', true);
    fixture.componentRef.setInput('gateBlocked', true);
    fixture.componentRef.setInput('episodeId', 'episode-1');
    fixture.detectChanges();
    await fixture.whenStable();
    expect(root.textContent).toContain(episodesVi.new.createLost);
    expect(root.querySelector('[role="status"]')?.textContent).toContain(episodesVi.research.gate.writeStoryBlocked);
    expect(root.querySelector('a[href="/episodes/episode-1/research"]')).not.toBeNull();
    expect(root.querySelector<HTMLButtonElement>('button[type="submit"]')?.disabled).toBe(true);
    let submitted = 0;
    onEmit(component.submitted, () => submitted++);
    root.querySelector('form')?.dispatchEvent(new Event('submit', { bubbles: true, cancelable: true }));
    expect(submitted).toBe(0);
  });

  it('interpolates 429 seconds and emits elapsed without allowing a submit during the countdown', async () => {
    const { fixture, root, component } = await setup();
    vi.useFakeTimers();
    let elapsed = 0;
    onEmit(component.countdownElapsed, () => elapsed++);
    fixture.componentRef.setInput('retryAfterSec', 2);
    fixture.detectChanges();
    expect(root.querySelector('nts-retry-countdown')?.textContent).toContain('2 giây');
    expect(root.querySelector<HTMLButtonElement>('button[type="submit"]')?.disabled).toBe(true);
    vi.advanceTimersByTime(2000);
    fixture.detectChanges();
    expect(elapsed).toBe(1);
  });
});
