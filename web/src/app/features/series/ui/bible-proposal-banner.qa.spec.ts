// QA (10.5.7-T1) — the proposal banner's op-normalisation tolerance for every wire spelling the plan leaves open,
// ops with no value member, the `{ n }` count, a heading title that must stay literal, XSS-shaped op text inside the
// deferred diff, an empty proposal, and the pending guard on both actions.
import { DeferBlockBehavior, DeferBlockState, TestBed } from '@angular/core/testing';
import { TranslocoTestingModule } from '@jsverse/transloco';
import { of } from 'rxjs';
import { afterEach, describe, expect, it } from 'vitest';
import coreEn from '../../../../assets/i18n/core/en.json';
import seriesEn from '../../../../assets/i18n/series/en.json';
import { ConfirmDialog } from '@shared/ui';
import { ZONELESS_TEST_PROVIDERS } from '../../../testing/providers';
import { onEmit } from '@shared/ui/testing/on-emit';
import type { BibleOp, BibleProposal } from '../domain/bible.model';
import { BibleProposalBanner } from './bible-proposal-banner';

const PREVIEW = { characters: 2, places: 1, rules: 3, summaryForEpisodeId: null } as const;

type OpLabel = 'summary' | 'character' | 'place' | 'rule' | 'other';
const OP_COPY: Record<OpLabel, string> = {
  summary: seriesEn.bible.op.summary,
  character: seriesEn.bible.op.character,
  place: seriesEn.bible.op.place,
  rule: seriesEn.bible.op.rule,
  other: seriesEn.bible.op.other,
};

function proposalOf(ops: readonly BibleOp[], overrides: Partial<BibleProposal> = {}): BibleProposal {
  return { basedOnVersion: 1, proposedFromEpisodeId: null, proposedAt: '', ops, preview: PREVIEW, ...overrides };
}

afterEach(() => TestBed.resetTestingModule());

async function setup(proposal: BibleProposal, title: string | null = 'An episode', pending = false) {
  let confirmations = 0;
  await TestBed.configureTestingModule({
    imports: [BibleProposalBanner, TranslocoTestingModule.forRoot({ langs: { en: { ...coreEn, series: seriesEn } }, translocoConfig: { availableLangs: ['en'], defaultLang: 'en' }, preloadLangs: true })],
    providers: [...ZONELESS_TEST_PROVIDERS, { provide: ConfirmDialog, useValue: { confirm: () => { confirmations += 1; return { afterClosed: () => of(true) }; } } }],
    deferBlockBehavior: DeferBlockBehavior.Manual,
  }).compileComponents();
  const fixture = TestBed.createComponent(BibleProposalBanner);
  fixture.componentRef.setInput('proposal', proposal);
  fixture.componentRef.setInput('version', 2);
  fixture.componentRef.setInput('proposedTitle', title);
  fixture.componentRef.setInput('pending', pending);
  await fixture.whenStable();
  return { fixture, component: fixture.componentInstance, root: fixture.nativeElement as HTMLElement, confirmations: () => confirmations };
}

describe('BibleProposalBanner (QA)', () => {
  it.each<[string, 'add' | 'change', OpLabel]>([
    ['AddCharacter', 'add', 'character'],
    ['add_character', 'add', 'character'],
    ['ADD-PLACE', 'add', 'place'],
    ['AddRule', 'add', 'rule'],
    ['AddSummary', 'add', 'summary'],
    ['addsummary', 'add', 'summary'],
    ['add', 'add', 'other'],
    ['add_', 'add', 'other'],
    ['remove_character', 'change', 'other'],
    ['ADD-PLACE!', 'add', 'place'],
    ['foo', 'change', 'other'],
    ['', 'change', 'other'],
  ])('normalises the wire kind %j to %s/%s', async (kind, expectedKind, opLabel) => {
    const { component } = await setup(proposalOf([{ kind, name: 'X', description: 'Y' }]));
    const [row] = component.rows();
    expect(row?.kind).toBe(expectedKind);
    expect(row?.label).toBe(OP_COPY[opLabel]);
    expect(row?.after).toBe('X');
  });

  it('keeps the documented value precedence name -> text -> summary -> description and an op with no value as an empty row', async () => {
    const { component } = await setup(proposalOf([
      { kind: 'AddCharacter', name: 'N', text: 'T', summary: 'S', description: 'D' },
      { kind: 'AddRule', text: 'T', summary: 'S', description: 'D' },
      { kind: 'AddSummary', summary: 'S', description: 'D' },
      { kind: 'AddPlace', description: 'D' },
      { kind: 'AddPlace' },
    ]));
    expect(component.rows().map((row) => row.after)).toEqual(['N', 'T', 'S', 'D', '']);
  });

  it('counts ops, not preview rows, in the banner heading', async () => {
    const { component } = await setup(proposalOf([{ kind: 'AddCharacter', name: 'A' }], { preview: { characters: 99, places: 0, rules: 0, summaryForEpisodeId: null } }));
    expect(component.heading()).toBe(seriesEn.bible.proposal.banner.replace('{{n}}', '1').replace('{{title}}', 'An episode'));
  });

  it('keeps a title with interpolation markers and replacement patterns literal', async () => {
    const { component, root } = await setup(proposalOf([{ kind: 'AddCharacter', name: 'A' }]), '{{n}} $& $\' $1');
    expect(component.heading()).toContain("{{n}} $& $' $1");
    expect(component.heading()).not.toContain('An episode');
    expect(root.querySelector('h2')?.textContent).toContain("{{n}} $& $' $1");
  });

  it('falls back to the no-title copy on a null and an empty title', async () => {
    const nullTitle = await setup(proposalOf([{ kind: 'AddCharacter', name: 'A' }]), null);
    expect(nullTitle.component.heading()).toBe(seriesEn.bible.proposal.bannerNoTitle.replace('{{n}}', '1'));
    TestBed.resetTestingModule();
    const emptyTitle = await setup(proposalOf([{ kind: 'AddCharacter', name: 'A' }]), '');
    expect(emptyTitle.component.heading()).toBe(seriesEn.bible.proposal.bannerNoTitle.replace('{{n}}', '1'));
  });

  it('renders XSS-shaped op values and reasons as text inside the deferred diff', async () => {
    const payload = '<img src=x onerror="globalThis.__bannerXss = 1">';
    const { fixture, root } = await setup(proposalOf([{ kind: 'AddCharacter', name: payload, reason: `${payload} {{title}}` }]));
    const [block] = await fixture.getDeferBlocks();
    await block!.render(DeferBlockState.Complete);
    await fixture.whenStable();

    expect(root.querySelector('img')).toBeNull();
    expect(root.querySelector('.diff__row ins')?.textContent).toContain(payload);
    expect(root.querySelector('.diff__reason')?.textContent).toContain("{{title}}");
    expect('__bannerXss' in globalThis).toBe(false);
  });

  it('renders an empty proposal without a diff row and with n = 0', async () => {
    const { component, root } = await setup(proposalOf([]));
    expect(component.heading()).toBe(seriesEn.bible.proposal.banner.replace('{{n}}', '0').replace('{{title}}', 'An episode'));
    expect(component.rows()).toEqual([]);
    expect(root.querySelectorAll('.diff__row')).toHaveLength(0);
    expect(root.querySelector('.proposal__counts dd')).not.toBeNull();
  });

  it('shows the base-version note only when the proposal is based on another version', async () => {
    const older = await setup(proposalOf([], { basedOnVersion: 1 }), 'An episode');
    expect(older.root.querySelector('.proposal__base')?.textContent).toContain('1');
    TestBed.resetTestingModule();
    const current = await setup(proposalOf([], { basedOnVersion: 2 }), 'An episode');
    expect(current.root.querySelector('.proposal__base')).toBeNull();
  });

  it('disables both actions while pending and never opens the dismiss dialog then', async () => {
    const { component, root, confirmations } = await setup(proposalOf([{ kind: 'AddCharacter', name: 'A' }]), 'An episode', true);
    const buttons = root.querySelectorAll<HTMLButtonElement>('.proposal__actions button');
    expect([...buttons].map((button) => button.disabled)).toEqual([true, true]);
    let dismissed = 0;
    onEmit(component.dismiss, () => { dismissed += 1; });
    await component.confirmDismiss();
    expect(confirmations()).toBe(0);
    expect(dismissed).toBe(0);
  });

  it('does not dismiss when the proposal changed while the confirmation was open', async () => {
    const { fixture, component, confirmations } = await setup(proposalOf([{ kind: 'AddCharacter', name: 'A' }]));
    let dismissed = 0;
    onEmit(component.dismiss, () => { dismissed += 1; });
    const asking = component.confirmDismiss();
    // A refetch replaced the proposal before the user answered.
    fixture.componentRef.setInput('proposal', proposalOf([{ kind: 'AddPlace', name: 'B' }]));
    await fixture.whenStable();
    await asking;
    expect(confirmations()).toBe(1);
    expect(dismissed).toBe(0);
  });
});
