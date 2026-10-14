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
import type { AppError } from '@core/errors';
import { ApproveDialogComponent } from './approve-dialog.component';

function failure(code: string, status = 409): AppError {
  return { code, status, kind: status === 429 ? 'rate_limit' : 'conflict', fieldErrors: {}, problem: { detail: 'DO-NOT-RENDER-DETAIL', errors: [{ segment: 'DO-NOT-RENDER-SEGMENT', index: 1, rule: 'DO-NOT-RENDER-RULE' }] } };
}

async function setup(lang: 'vi' | 'en' = 'vi') {
  await TestBed.configureTestingModule({
    imports: [ApproveDialogComponent, TranslocoTestingModule.forRoot({
      langs: { en: { ...coreEn, episodes: episodesEn, errors: errorsEn }, vi: { ...coreVi, episodes: episodesVi, errors: errorsVi }, 'episodes/en': episodesEn, 'episodes/vi': episodesVi, 'errors/en': errorsEn, 'errors/vi': errorsVi },
      translocoConfig: { availableLangs: ['vi', 'en'], defaultLang: lang, fallbackLang: lang, reRenderOnLangChange: true },
      preloadLangs: true,
    })],
    providers: [...ZONELESS_TEST_PROVIDERS],
  }).compileComponents();
  const fixture = TestBed.createComponent(ApproveDialogComponent);
  fixture.componentRef.setInput('episodeId', 'episode-a');
  await fixture.whenStable();
  return { fixture, component: fixture.componentInstance, root: fixture.nativeElement as HTMLElement };
}

const confirmButton = (root: HTMLElement) => root.querySelector<HTMLButtonElement>('.confirm-dialog__actions button:last-child')!;
afterEach(() => TestBed.resetTestingModule());

describe('ApproveDialogComponent', () => {
  it('prevents confirm while the host blocks it or a request is pending', async () => {
    const { fixture, root, component } = await setup();
    let approved = 0;
    onEmit(component.approved, () => approved++);
    fixture.componentRef.setInput('confirmDisabled', true);
    await fixture.whenStable();
    expect(confirmButton(root).disabled).toBe(true);
    confirmButton(root).click();
    expect(approved).toBe(0);
    fixture.componentRef.setInput('confirmDisabled', false);
    await fixture.whenStable();
    const confirm = confirmButton(root);
    confirm.focus();
    fixture.componentRef.setInput('confirmDisabled', true);
    fixture.componentRef.setInput('busy', true);
    await fixture.whenStable();
    expect(confirm.disabled).toBe(false);
    expect(confirm.getAttribute('aria-disabled')).toBe('true');
    expect(document.activeElement).toBe(confirm);
    confirm.click();
    expect(approved).toBe(0);
    expect(root.querySelector('.confirm-dialog__pending')?.textContent?.trim()).toBe(TestBed.inject(TranslocoService).translate('core.ui.confirm.pending'));
    fixture.componentRef.setInput('busy', false);
    fixture.componentRef.setInput('confirmDisabled', false);
    await fixture.whenStable();
    confirm.click();
    expect(approved).toBe(1);
  });

  it.each(['vi', 'en'] as const)('shows advisory hints without disabling confirm or showing a cost line in %s', async (lang) => {
    const { fixture, root } = await setup(lang);
    fixture.componentRef.setInput('hintCount', 3);
    await fixture.whenStable();
    expect(root.querySelector('.approval-hint')?.textContent?.trim()).toBe(TestBed.inject(TranslocoService).translate('episodes.approve.hint', { count: 3 }));
    expect(confirmButton(root).disabled).toBe(false);
    expect(root.querySelector('[cost-line], #dynamic-cost')).toBeNull();
    expect(root.textContent).not.toMatch(/\b(credit|credits|cost|price|balance)\b|tín dụng|chi phí|số dư/i);
  });

  it.each(['episode_invalid_state', 'story_fact_sheet_outdated', 'story_sources_required', 'story_too_similar_to_source', 'story_moderated', 'keywords_flagged'])('maps %s to its frozen error key without exposing problem details', async (code) => {
    const { fixture, root } = await setup();
    fixture.componentRef.setInput('blocked', failure(code));
    await fixture.whenStable();
    expect(root.querySelector('[role="alert"]')?.textContent?.trim()).toBe(TestBed.inject(TranslocoService).translate(`errors.${code}`));
    expect(root.textContent).not.toContain('DO-NOT-RENDER');
    // The research route is not mounted yet: never send the user to an unimplemented route.
    expect(root.querySelector('a')).toBeNull();
  });

  it.each([
    ['not_found', 404, 'errors.not_found'],
    ['internal_error', 500, 'errors.internal_error'],
    ['unexpected_code', 409, 'errors.unknown_error'],
  ] as const)('uses the canonical mapped key for %s', async (code, status, key) => {
    const { fixture, root } = await setup();
    fixture.componentRef.setInput('blocked', failure(code, status));
    await fixture.whenStable();
    expect(root.querySelector('[role="alert"]')?.textContent?.trim()).toBe(TestBed.inject(TranslocoService).translate(key));
  });

  it('shows an attestation-required error only beside the checkbox, never as a duplicate alert', async () => {
    const { fixture, root } = await setup();
    fixture.componentRef.setInput('attestation', { required: true, textVersion: 'v1', text: 'Statement', acceptedAt: null });
    fixture.componentRef.setInput('checkboxInvalid', true);
    fixture.componentRef.setInput('blocked', failure('story_attestation_required', 400));
    await fixture.whenStable();
    expect(root.querySelector('[role="alert"]')).toBeNull();
    expect(root.querySelector('.attestation__error')?.textContent?.trim()).toBe(TestBed.inject(TranslocoService).translate('errors.story_attestation_required'));
  });

  it('renders a required statement, forwards acceptance and removes the block when not required', async () => {
    const { fixture, root, component } = await setup();
    const statement = { required: true, textVersion: 'v1', text: 'Statement', acceptedAt: null };
    fixture.componentRef.setInput('attestation', statement);
    fixture.componentRef.setInput('checkboxInvalid', true);
    await fixture.whenStable();
    const ticks: boolean[] = [];
    onEmit(component.acceptedChange, (value) => ticks.push(value));
    expect(root.querySelector('nts-attestation-block')).not.toBeNull();
    root.querySelector<HTMLInputElement>('input[type="checkbox"]')!.click();
    await fixture.whenStable();
    expect(ticks).toEqual([true]);
    fixture.componentRef.setInput('attestation', { ...statement, required: false });
    await fixture.whenStable();
    expect(root.querySelector('nts-attestation-block')).toBeNull();
  });

  it('shows a countdown and blocks confirmation until the countdown is cleared', async () => {
    const { fixture, root, component } = await setup();
    let approved = 0;
    onEmit(component.approved, () => approved++);
    const confirm = confirmButton(root);
    confirm.focus();
    fixture.componentRef.setInput('blocked', failure('rate_limited', 429));
    fixture.componentRef.setInput('retryAfterSec', 2);
    fixture.componentRef.setInput('confirmDisabled', true);
    await fixture.whenStable();
    expect(root.querySelector('nts-retry-countdown')).not.toBeNull();
    expect(confirm.disabled).toBe(false);
    expect(confirm.getAttribute('aria-disabled')).toBe('true');
    expect(document.activeElement).toBe(confirm);
    confirm.click();
    expect(approved).toBe(0);
    fixture.componentRef.setInput('retryAfterSec', 0);
    fixture.componentRef.setInput('confirmDisabled', false);
    await fixture.whenStable();
    expect(confirm.disabled).toBe(false);
    expect(confirm.getAttribute('aria-disabled')).toBeNull();
    confirm.click();
    expect(approved).toBe(1);
  });

  it('cancels on Escape or backdrop and keeps cancellation guarded while busy', async () => {
    const { fixture, root, component } = await setup();
    let cancelled = 0;
    onEmit(component.cancelled, () => cancelled++);
    expect(root.querySelectorAll('.cdk-focus-trap-anchor')).toHaveLength(2);
    root.querySelector<HTMLElement>('.confirm-dialog')!.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true }));
    root.querySelector<HTMLButtonElement>('.approval-backdrop')!.click();
    expect(cancelled).toBe(2);
    fixture.componentRef.setInput('busy', true);
    await fixture.whenStable();
    root.querySelector<HTMLButtonElement>('.approval-backdrop')!.click();
    root.querySelector<HTMLElement>('.confirm-dialog')!.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true }));
    expect(cancelled).toBe(2);
  });
});
