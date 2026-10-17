import { DeferBlockBehavior, DeferBlockState, TestBed } from '@angular/core/testing';
import { TranslocoTestingModule } from '@jsverse/transloco';
import { of } from 'rxjs';
import { afterEach, describe, expect, it } from 'vitest';
import coreEn from '../../../../assets/i18n/core/en.json';
import seriesEn from '../../../../assets/i18n/series/en.json';
import { ConfirmDialog } from '@shared/ui';
import { ZONELESS_TEST_PROVIDERS } from '../../../testing/providers';
import { onEmit } from '@shared/ui/testing/on-emit';
import type { BibleProposal } from '../domain/bible.model';
import { BibleProposalBanner } from './bible-proposal-banner';

const proposal: BibleProposal = { basedOnVersion: 1, proposedFromEpisodeId: null, proposedAt: '', preview: { characters: 7, places: 4, rules: 2, summaryForEpisodeId: null }, ops: [{ kind: 'add_character', name: 'Lan', reason: 'First appearance' }, { kind: 'AddPlace', name: 'Lake' }, { kind: 'unforeseen', description: 'Other detail', reason: 'Reason' }] };
afterEach(() => TestBed.resetTestingModule());
async function setup(title: string | null = 'An episode') {
  let confirmations = 0;
  await TestBed.configureTestingModule({ imports: [BibleProposalBanner, TranslocoTestingModule.forRoot({ langs: { en: { ...coreEn, series: seriesEn } }, translocoConfig: { availableLangs: ['en'], defaultLang: 'en' }, preloadLangs: true })], providers: [...ZONELESS_TEST_PROVIDERS, { provide: ConfirmDialog, useValue: { confirm: () => { confirmations++; return { afterClosed: () => of(true) }; } } }], deferBlockBehavior: DeferBlockBehavior.Manual }).compileComponents();
  const fixture = TestBed.createComponent(BibleProposalBanner);
  fixture.componentRef.setInput('proposal', proposal); fixture.componentRef.setInput('version', 2); fixture.componentRef.setInput('proposedTitle', title);
  await fixture.whenStable();
  return { fixture, component: fixture.componentInstance, root: fixture.nativeElement as HTMLElement, confirmations: () => confirmations };
}
describe('BibleProposalBanner', () => {
  it('uses server preview counts rather than counting operation kinds', async () => {
    const { root } = await setup();
    expect([...root.querySelectorAll('dd')].map(node => node.textContent?.trim())).toEqual(['7', '4', '2']);
    expect(root.querySelector('[role=status]')).not.toBeNull();
  });
  it('maps normalisation-tolerant op kinds and reasons into the shared deferred rows renderer', async () => {
    const { fixture, component, root } = await setup();
    expect(component.rows().map(row => row.kind)).toEqual(['add', 'add', 'change']);
    expect(component.rows().map(row => row.after)).toEqual(['Lan', 'Lake', 'Other detail']);
    expect(component.rows().every(row => row.label.length > 0)).toBe(true);
    expect(component.rows()[0]?.reason).toContain('First appearance');
    expect(root.querySelector('nts-diff-view')).toBeNull();
    const [block] = await fixture.getDeferBlocks();
    await block!.render(DeferBlockState.Complete); await fixture.whenStable();
    expect(root.querySelectorAll('nts-diff-view .diff__row')).toHaveLength(3);
    expect(root.querySelector('nts-diff-view .diff__reason')?.textContent).toContain('First appearance');
  });
  it('falls back without a title and keeps arbitrary title text out of translation interpolation', async () => {
    const { fixture, component, root } = await setup(null);
    expect(component.heading()).toBe(seriesEn.bible.proposal.bannerNoTitle.replace('{{n}}', '3'));
    fixture.componentRef.setInput('proposedTitle', '{{title}} <b>literal</b>'); await fixture.whenStable();
    expect(root.querySelector('h2')?.textContent).toContain('{{title}} <b>literal</b>');
    expect(root.querySelector('h2 b')).toBeNull();
  });
  it('orders Accept before Dismiss and confirms before emitting dismissal', async () => {
    const { root, component, confirmations } = await setup();
    const events: string[] = []; onEmit(component.accept, () => events.push('accept')); onEmit(component.dismiss, () => events.push('dismiss'));
    const buttons = root.querySelectorAll<HTMLButtonElement>('button');
    buttons[0]!.click(); expect(events).toEqual(['accept']);
    buttons[1]!.click(); await Promise.resolve();
    expect(confirmations()).toBe(1); expect(events).toEqual(['accept', 'dismiss']);
  });
  it('reserves the diff region for loading and error states', async () => {
    const { fixture, root } = await setup(); const [block] = await fixture.getDeferBlocks();
    expect(root.querySelector('.proposal__placeholder')).not.toBeNull();
    await block!.render(DeferBlockState.Error); await fixture.whenStable();
    expect(root.querySelector('.proposal__diff nts-error-state')).not.toBeNull();
  });
});
