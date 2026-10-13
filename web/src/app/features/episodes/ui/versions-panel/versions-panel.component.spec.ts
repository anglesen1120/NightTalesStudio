import { DeferBlockBehavior, DeferBlockState, TestBed } from '@angular/core/testing';
import { TranslocoService, TranslocoTestingModule } from '@jsverse/transloco';
import { afterEach, describe, expect, it } from 'vitest';
import { AppErrorMapper, type AppError } from '@core/errors';
import { onEmit } from '@shared/ui/testing/on-emit';
import { DiffViewComponent } from '@shared/ui/diff-view/diff-view.component';
import { By } from '@angular/platform-browser';
import { ZONELESS_TEST_PROVIDERS } from '../../../../testing/providers';
import coreVi from '../../../../../assets/i18n/core/vi.json';
import episodesVi from '../../../../../assets/i18n/episodes/vi.json';
import episodesEn from '../../../../../assets/i18n/episodes/en.json';
import errorsVi from '../../../../../assets/i18n/errors/vi.json';
import type { StoryVersionDetail } from '../../domain/story-version.model';
import { VersionsPanelComponent } from './versions-panel.component';

const left: StoryVersionDetail = { versionId: 'older', version: 1, source: 'generated', createdAt: '2026-10-01T12:00:00Z', wordCount: 2, etag: 'old-etag', similarityScore: null, approved: false, isCurrent: false, body: 'old word', content: null, createdBy: 'owner' };
const right: StoryVersionDetail = { ...left, versionId: 'current', version: 2, source: 'edited', similarityScore: 0.5, approved: true, isCurrent: true, body: 'new word' };
const failure: AppError = { code: 'network_error', kind: 'network', status: 0, fieldErrors: {}, problem: {} };

async function setup() {
  await TestBed.configureTestingModule({
    imports: [VersionsPanelComponent, TranslocoTestingModule.forRoot({
      langs: { vi: coreVi, en: coreVi, 'core/vi': coreVi, 'episodes/vi': episodesVi, 'episodes/en': episodesEn, 'errors/vi': errorsVi },
      translocoConfig: { availableLangs: ['vi', 'en'], defaultLang: 'vi', fallbackLang: 'vi', reRenderOnLangChange: true }, preloadLangs: true,
    })],
    providers: [...ZONELESS_TEST_PROVIDERS, { provide: AppErrorMapper, useValue: { messageKey: () => 'errors.network_error', traceFooter: () => null, fieldMessageKey: () => 'errors.network_error' } }],
    deferBlockBehavior: DeferBlockBehavior.Manual,
  }).compileComponents();
  const fixture = TestBed.createComponent(VersionsPanelComponent);
  fixture.componentRef.setInput('versions', [right, left]);
  fixture.componentRef.setInput('loaded', true);
  fixture.componentRef.setInput('left', left);
  fixture.componentRef.setInput('right', right);
  fixture.componentRef.setInput('leftId', left.versionId);
  fixture.componentRef.setInput('rightId', right.versionId);
  fixture.componentRef.setInput('diff', { segments: [], added: 1, removed: 1, truncated: false });
  fixture.componentRef.setInput('canRestore', true);
  fixture.detectChanges();
  await fixture.whenStable();
  return { fixture, root: fixture.nativeElement as HTMLElement, component: fixture.componentInstance };
}

afterEach(() => TestBed.resetTestingModule());

describe('VersionsPanelComponent', () => {
  it('lists server order, localized metadata and icon-plus-text badges; slots emit the intended side', async () => {
    const { fixture, root, component } = await setup();
    const rows = root.querySelectorAll<HTMLButtonElement>('.versions__row');
    expect([...rows].map((row) => row.dataset['versionId'])).toEqual(['current', 'older']);
    expect(rows[0]?.querySelectorAll('.versions__badge')).toHaveLength(2);
    expect(rows[0]?.querySelector('nts-icon-check')).not.toBeNull();
    expect(rows[0]?.querySelector('nts-icon-clock')).not.toBeNull();
    expect(root.querySelector('time')?.textContent).toBe(new Intl.DateTimeFormat('vi-VN', { dateStyle: 'short', timeStyle: 'short' }).format(new Date(right.createdAt)));
    TestBed.inject(TranslocoService).setActiveLang('en');
    fixture.detectChanges(); await fixture.whenStable();
    expect(root.querySelector('time')?.textContent).toBe(new Intl.DateTimeFormat('en-US', { dateStyle: 'short', timeStyle: 'short' }).format(new Date(right.createdAt)));
    const choices: { versionId: string; side: 'left' | 'right' }[] = [];
    onEmit(component.selectVersion, (value) => choices.push(value));
    rows[1]!.click();
    root.querySelectorAll<HTMLButtonElement>('.versions__slots button')[1]!.click();
    rows[0]!.click();
    expect(choices).toEqual([{ versionId: 'older', side: 'left' }, { versionId: 'current', side: 'right' }]);
  });

  it('disables Restore whenever the host disallows it or a restore is in flight', async () => {
    const { fixture, root, component } = await setup();
    let requests = 0;
    onEmit(component.restore, () => requests++);
    const button = root.querySelector<HTMLButtonElement>('.versions__restore')!;
    expect(button.disabled).toBe(false);
    button.click(); expect(requests).toBe(1);
    fixture.componentRef.setInput('canRestore', false);
    fixture.detectChanges(); await fixture.whenStable();
    expect(button.disabled).toBe(true); button.click(); expect(requests).toBe(1);
    fixture.componentRef.setInput('canRestore', true);
    fixture.componentRef.setInput('restoring', true);
    fixture.detectChanges(); await fixture.whenStable();
    expect(button.disabled).toBe(true);
    expect(button.getAttribute('aria-busy')).toBe('true');
  });

  it('defers the diff, emits once per pair and announces only the summary politely', async () => {
    const { fixture, root } = await setup();
    expect(root.querySelector('nts-diff-view')).toBeNull();
    expect(root.querySelector('.versions__compare')?.tagName).toBe('BUTTON');
    const blocks = await fixture.getDeferBlocks();
    await blocks[0]!.render(DeferBlockState.Complete);
    fixture.detectChanges(); await fixture.whenStable();
    const diff = fixture.debugElement.query(By.directive(DiffViewComponent)).componentInstance as DiffViewComponent;
    const emissions: unknown[] = [];
    onEmit(diff.rendered, (value) => emissions.push(value));
    fixture.detectChanges(); await fixture.whenStable();
    expect(emissions).toHaveLength(0);
    const summary = root.querySelector('.versions__summary')!;
    expect(summary.getAttribute('aria-live')).toBe('polite');
    expect(summary.textContent?.trim()).toBe(TestBed.inject(TranslocoService).translate('episodes.versions.diff.summary', { added: 1, removed: 1 }));
    fixture.componentRef.setInput('right', { ...right, body: left.body });
    fixture.detectChanges(); await fixture.whenStable();
    expect(emissions).toHaveLength(1);
    expect(summary.textContent?.trim()).toBe(TestBed.inject(TranslocoService).translate('episodes.versions.noDifferences'));
    expect(root.querySelector('.diff')?.getAttribute('aria-live')).toBe('off');
  });

  it('shows empty, same-selection and truncated states without a misleading comparison', async () => {
    const { fixture, root } = await setup();
    fixture.componentRef.setInput('sameSelected', true);
    fixture.detectChanges(); await fixture.whenStable();
    expect(root.querySelector('.versions__compare')).toBeNull();
    fixture.componentRef.setInput('sameSelected', false);
    fixture.componentRef.setInput('diff', { segments: [], added: 5, removed: 5, truncated: true });
    fixture.detectChanges(); await fixture.whenStable();
    expect(root.querySelector('.versions__compare')).toBeNull();
    expect(root.textContent).toContain(TestBed.inject(TranslocoService).translate('episodes.versions.truncated'));
    fixture.componentRef.setInput('empty', true);
    fixture.detectChanges(); await fixture.whenStable();
    expect(root.querySelector('nts-empty-state')).not.toBeNull();
    expect(root.querySelector('nts-error-state')).toBeNull();
  });

  it('retries only the failing side or list and exposes message-only reopen plus restore errors', async () => {
    const { fixture, root, component } = await setup();
    const sides: string[] = []; let lists = 0;
    onEmit(component.retrySide, (side) => sides.push(side));
    onEmit(component.retryList, () => lists++);
    fixture.componentRef.setInput('sideError', { side: 'right', error: failure });
    fixture.componentRef.setInput('needsReopen', true);
    fixture.componentRef.setInput('restoreError', failure);
    fixture.detectChanges(); await fixture.whenStable();
    root.querySelector<HTMLButtonElement>('.versions__side-error button')!.click();
    expect(sides).toEqual(['right']); expect(lists).toBe(0);
    expect(root.querySelector('.versions__alert')?.getAttribute('role')).toBe('alert');
    expect(root.querySelector('.versions__alert button')).toBeNull();
    expect(root.querySelector('nts-form-error-alert')).not.toBeNull();
    fixture.componentRef.setInput('error', failure);
    fixture.detectChanges(); await fixture.whenStable();
    root.querySelector<HTMLButtonElement>('nts-error-state button')!.click();
    expect(lists).toBe(1);
  });

  it('focuses the heading on open and emits closed from Escape and the close button', async () => {
    const { fixture, root, component } = await setup();
    document.body.appendChild(root);
    root.querySelector<HTMLElement>('h2')!.focus();
    expect(document.activeElement).toBe(root.querySelector('h2'));
    let closes = 0; onEmit(component.closed, () => closes++);
    root.querySelector<HTMLButtonElement>('.versions__close')!.click();
    root.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true }));
    expect(closes).toBe(2);
    fixture.destroy(); root.remove();
  });
});
