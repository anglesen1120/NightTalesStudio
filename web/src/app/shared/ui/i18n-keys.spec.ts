import { ChangeDetectionStrategy, Component } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { TranslocoService } from '@jsverse/transloco';
import { describe, expect, it } from 'vitest';
import { en, uiTestingTranslations, vi } from './testing/ui-testing-translations';
import { EmptyState } from './empty-state/empty-state';
import { ErrorState } from './error-state/error-state';
import { LoadingState } from './loading-state/loading-state';
import { PageHeader } from './page-header/page-header';
import { StatusChip } from './status-chip/status-chip';
import { Stepper, type StepperStep } from './stepper/stepper';

function flatten(value: unknown, prefix = ''): string[] {
  if (typeof value === 'string') return [prefix];
  if (typeof value !== 'object' || value === null) return [];
  return Object.entries(value).flatMap(([key, child]) => flatten(child, prefix ? `${prefix}.${key}` : key));
}

const required = [
  'core.ui.pageHeader.back', 'core.ui.pageHeader.actions', 'core.ui.emptyState.title', 'core.ui.emptyState.body',
  'core.ui.errorState.title', 'core.ui.errorState.body', 'core.ui.errorState.retry', 'core.ui.errorState.traceId',
  'core.ui.loadingState.label', 'core.ui.confirm.title', 'core.ui.confirm.cancel', 'core.ui.confirm.confirm',
  'core.ui.dialog.close', 'core.ui.statusChip.unknown', 'core.ui.stepper.step', 'core.ui.stepper.current',
  'core.ui.theme.toggle', 'core.ui.theme.dark', 'core.ui.theme.light', 'core.ui.charCounter.label',
  'core.ui.charCounter.over', 'core.ui.copyableCode.copy', 'core.ui.copyableCode.copied',
  'core.ui.icons.check', 'core.ui.icons.clock', 'core.ui.icons.alert', 'core.ui.icons.play',
  'core.ui.icons.pause', 'core.ui.icons.copy', 'core.ui.icons.unknown',
];

@Component({
  imports: [PageHeader, EmptyState, ErrorState, LoadingState, StatusChip, Stepper],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <nts-page-header titleKey="core.ui.emptyState.title" />
    <nts-empty-state titleKey="core.ui.emptyState.title" bodyKey="core.ui.emptyState.body" />
    <nts-error-state bodyKey="core.ui.errorState.body" />
    <nts-loading-state />
    <nts-status-chip statusKey="draft" />
    <nts-stepper [steps]="steps" />
  `,
})
class BilingualHost {
  readonly steps: readonly StepperStep[] = [{ labelKey: 'core.ui.theme.dark', state: 'current' }];
}

describe('core UI i18n keys', () => {
  it('keeps identical vi and en key sets with every kit key', () => {
    const viKeys = flatten(vi).sort();
    const enKeys = flatten(en).sort();
    expect(enKeys).toEqual(viKeys);
    for (const key of required) expect(viKeys).toContain(key);
  });

  it('resolves every required key in both languages through the Transloco test double', async () => {
    await TestBed.configureTestingModule({ imports: [uiTestingTranslations()] }).compileComponents();
    const transloco = TestBed.inject(TranslocoService);
    for (const lang of ['vi', 'en']) {
      transloco.setActiveLang(lang);
      for (const key of required) expect(transloco.translate(key)).not.toBe(key);
    }
  });

  it('renders every page/state primitive in vi and en', async () => {
    await TestBed.configureTestingModule({ imports: [BilingualHost, uiTestingTranslations()] }).compileComponents();
    const fixture = TestBed.createComponent(BilingualHost);
    const transloco = TestBed.inject(TranslocoService);
    for (const [lang, expected] of [
      ['vi', ['Chưa có nội dung', 'Hãy thử lại', 'Đang tải', 'Bản nháp', 'Tối']],
      ['en', ['Nothing here yet', 'Try again', 'Loading', 'Draft', 'Dark']],
    ] as const) {
      transloco.setActiveLang(lang);
      fixture.detectChanges();
      await fixture.whenStable();
      const text = (fixture.nativeElement as HTMLElement).textContent ?? '';
      for (const value of expected) expect(text).toContain(value);
    }
  });
});
