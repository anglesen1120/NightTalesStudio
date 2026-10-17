import { DeferBlockBehavior, DeferBlockState, TestBed } from '@angular/core/testing';
import { TranslocoTestingModule } from '@jsverse/transloco';
import { afterEach, describe, expect, it } from 'vitest';
import { onEmit } from '@shared/ui/testing/on-emit';
import type { SegmentAddress } from '@shared/util/segment-address';
import { ZONELESS_TEST_PROVIDERS } from '../../../testing/providers';
import episodesEn from '../../../../assets/i18n/episodes/en.json';
import qualityEn from '../../../../assets/i18n/quality/en.json';
import storyEn from '../../../../assets/i18n/story/en.json';
import type { ReviewIssue } from '../domain/audit.model';
import { ReviewPanelComponent } from './review-panel.component';
import type { ReviewPanelViewModel } from './review-panel.view-model';

const translations = TranslocoTestingModule.forRoot({
  langs: { en: {}, 'episodes/en': episodesEn, 'quality/en': qualityEn, 'story/en': storyEn },
  translocoConfig: { availableLangs: ['en'], defaultLang: 'en', fallbackLang: 'en' },
  preloadLangs: true,
});
const objective: ReviewIssue = { id: 'R-01', type: 'hook_missing', kind: 'objective', severity: 'blocking', segment: 'hook', impact: 'Missing setup', evidence: '<b>x</b>', repairDirection: 'Add a concrete opening.' };
const model: ReviewPanelViewModel = {
  counts: { blocking: 2, advisory: 3 },
  scores: { hook: 1, pacing: 2, atmosphere: 3, natural_language: 4, originality: 5, factual_care: 4, length_fit: null },
  stale: false, whyIssues: [objective], driftNote: false,
  similarityRows: [{ address: 'p:3', share: 0.21, level: 'warn', addressLabelKey: 'episodes.editor.segment.paragraph', addressLabelIndex: 4, addressText: null }], similarityCorpus: 'sources', similarityScore: 0.1,
  similarityUnavailable: false, hasAudit: true, partial: false,
};
async function setup(overrides: Partial<ReviewPanelViewModel> = {}, render = true) {
  await TestBed.configureTestingModule({ imports: [ReviewPanelComponent, translations], providers: ZONELESS_TEST_PROVIDERS, deferBlockBehavior: DeferBlockBehavior.Manual }).compileComponents();
  const fixture = TestBed.createComponent(ReviewPanelComponent);
  for (const [key, value] of Object.entries({ ...model, ...overrides })) fixture.componentRef.setInput(key, value);
  await fixture.whenStable();
  if (render) {
    const [block] = await fixture.getDeferBlocks();
    await block!.render(DeferBlockState.Complete);
    await fixture.whenStable();
  }
  return { fixture, root: fixture.nativeElement as HTMLElement, component: fixture.componentInstance };
}
async function expand(root: HTMLElement, fixture: { whenStable(): Promise<unknown> }) {
  root.querySelector<HTMLButtonElement>('.review-panel__disclosure')!.click();
  await fixture.whenStable();
}
afterEach(() => TestBed.resetTestingModule());

describe('ReviewPanelComponent', () => {
  it('renders supplied hint counts without deriving them from audit issues', async () => {
    const { root } = await setup();
    expect(root.querySelector('.review-panel__counts')?.textContent).toContain('Must fix: 2');
    expect(root.querySelector('.review-panel__counts')?.textContent).toContain('Worth a look: 3');
    expect(root.querySelector('[aria-live]')).toBeNull();
    expect(root.querySelector('nts-inline-annotation')).toBeNull();
  });

  it('uses a caption, column headers and seven score rows with row headers', async () => {
    const { root } = await setup();
    expect(root.querySelector('caption')?.textContent).toBe(episodesEn.audit.scores);
    expect(root.querySelectorAll('thead th[scope="col"]')).toHaveLength(2);
    expect(root.querySelectorAll('tbody tr')).toHaveLength(7);
    expect(root.querySelectorAll('tbody th[scope="row"]')).toHaveLength(7);
    expect([...root.querySelectorAll('tbody td')].map((cell) => cell.textContent)).toEqual(['1', '2', '3', '4', '5', '4', '—']);
  });

  it('keeps stale scores visible with their linked textual explanation and drift note', async () => {
    const { root } = await setup({ stale: true, driftNote: true });
    expect(root.querySelector('#story-review-audit-stale')?.textContent).toBe(qualityEn.model_review_stale);
    expect(root.querySelector('#story-review-audit-drift')?.textContent).toBe(episodesEn.audit.drift);
    expect(root.querySelector('table')?.getAttribute('aria-describedby')).toBe('story-review-audit-stale');
    expect(root.querySelectorAll('tbody tr')).toHaveLength(7);
    expect(root.querySelector('tbody td')?.textContent).toBe('1');
  });

  it('shows percentages and emits focus intent without navigating', async () => {
    const { root, component } = await setup();
    const addresses: SegmentAddress[] = [];
    onEmit(component.focusRequested, (address) => addresses.push(address));
    const row = root.querySelector<HTMLButtonElement>('.review-panel__similarity button')!;
    expect(row.textContent).toBe('Segment 4 — 21%');
    expect(root.textContent).toContain(storyEn.similarity.warn);
    row.click();
    expect(addresses).toEqual(['p:3']);
    expect(root.querySelector('a')).toBeNull();
  });

  it('renders past-episode overlaps as informational even if a row carries a block level', async () => {
    const { root } = await setup({ similarityCorpus: 'past_episodes', similarityRows: [{ address: 'p:3', share: 0.21, level: 'block', addressLabelKey: 'episodes.editor.segment.paragraph', addressLabelIndex: 4, addressText: null }] });
    expect(root.textContent).toContain(episodesEn.similarity.pastEpisodes);
    expect(root.textContent).toContain('Segment 4 — 21%');
    expect(root.textContent).not.toContain(storyEn.similarity.block);
    expect(root.textContent).not.toContain(storyEn.similarity.warn);
  });

  it('shows the raw address as plain text for a similarity row that does not parse', async () => {
    const { root } = await setup({ similarityRows: [{ address: 'x:9' as never, share: 0.5, level: null, addressLabelKey: 'episodes.editor.segment.paragraph', addressLabelIndex: null, addressText: 'x:9' }] });
    expect(root.textContent).toContain('Segment x:9 — 50%');
    expect([...root.querySelectorAll('button')].some((button) => button.textContent?.includes('x:9'))).toBe(false);
  });

  it('renders unavailable copy without segment claims, and no similarity UI for empty inputs', async () => {
    const { root, fixture } = await setup({ similarityUnavailable: true });
    expect(root.textContent).toContain(storyEn.similarity.unavailable);
    expect(root.querySelector('.review-panel__similarity button')).toBeNull();
    fixture.componentRef.setInput('similarityUnavailable', false);
    fixture.componentRef.setInput('similarityScore', null);
    fixture.componentRef.setInput('similarityRows', []);
    await fixture.whenStable();
    expect(root.querySelector('.review-panel__similarity')).toBeNull();
  });

  it('shows no checks recorded in the empty body and expanded why without zero counts', async () => {
    const { root, fixture } = await setup({ hasAudit: false, scores: null, whyIssues: [], similarityRows: [], similarityScore: null });
    expect(root.querySelector('.review-panel__counts')).toBeNull();
    expect(root.querySelector('.review-panel__empty')?.textContent).toBe(episodesEn.audit.noChecks);
    await expand(root, fixture);
    expect(root.querySelector('#story-review-audit-why')?.textContent).toContain(episodesEn.audit.noChecks);
    expect(root.querySelector('[role="alert"]')).toBeNull();
  });

  it('preserves header and explanations before the deferred body, then renders scores before similarity and why', async () => {
    const { root, fixture } = await setup({ driftNote: true }, false);
    expect(root.querySelector('h2')?.textContent).toBe(episodesEn.audit.heading);
    expect(root.querySelector('.review-panel__placeholder')).not.toBeNull();
    expect(root.querySelector('table')).toBeNull();
    expect(root.querySelector('#story-review-audit-drift')).not.toBeNull();
    const [block] = await fixture.getDeferBlocks();
    await block!.render(DeferBlockState.Complete);
    await fixture.whenStable();
    const table = root.querySelector('table')!;
    const similarity = root.querySelector('.review-panel__similarity')!;
    const disclosure = root.querySelector('.review-panel__disclosure')!;
    expect(table.compareDocumentPosition(similarity) & Node.DOCUMENT_POSITION_FOLLOWING).not.toBe(0);
    expect(similarity.compareDocumentPosition(disclosure) & Node.DOCUMENT_POSITION_FOLLOWING).not.toBe(0);
  });

  it('uses a keyboard disclosure and interpolates evidence, impact and repair as plain text', async () => {
    const { root, fixture } = await setup({ partial: true, whyIssues: [{ ...objective, impact: '<i>impact</i>', repairDirection: '<img src=x>' }] });
    const button = root.querySelector<HTMLButtonElement>('.review-panel__disclosure')!;
    expect(button.getAttribute('aria-expanded')).toBe('false');
    expect(button.getAttribute('aria-controls')).toBe('story-review-audit-why');
    expect(root.querySelector<HTMLElement>('#story-review-audit-why')?.hidden).toBe(true);
    await expand(root, fixture);
    expect(button.getAttribute('aria-expanded')).toBe('true');
    const details = root.querySelector('#story-review-audit-why')!;
    expect(details.textContent).toContain('<b>x</b>');
    expect(details.textContent).toContain('<i>impact</i>');
    expect(details.textContent).toContain('<img src=x>');
    expect(details.querySelector('b, i, img')).toBeNull();
    expect(root.textContent).toContain(episodesEn.audit.partial);
  });

  it('renders supplied advisory taste severity without adding an inline annotation', async () => {
    const { root, fixture } = await setup({ whyIssues: [{ ...objective, kind: 'taste', severity: 'advisory' }] });
    await expand(root, fixture);
    expect(root.querySelector('.review-panel__issues strong')?.textContent).toBe(episodesEn.audit.advisory);
    expect(root.querySelector('nts-inline-annotation')).toBeNull();
  });
});
