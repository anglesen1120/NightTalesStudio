import { TestBed } from '@angular/core/testing';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { ZONELESS_TEST_PROVIDERS } from '../../../../testing/providers';
import type { SourceCandidate } from '../../domain/source-candidate.model';
import { researchCopies, researchTranslations } from '../../testing/research-ui.testing';
import { SourceFinderPanelComponent } from './source-finder-panel.component';

function candidate(overrides: Partial<SourceCandidate> = {}): SourceCandidate {
  return { id: 'candidate-a', url: 'https://vnexpress.net/story', title: 'Public record', siteName: 'vnexpress.net', rank: 1, status: 'proposed', sourceId: null, createdAt: '2026-10-17T00:00:00Z', ...overrides };
}
async function setup(lang: 'vi' | 'en' = 'en', inputs: Record<string, unknown> = {}) {
  await TestBed.configureTestingModule({ imports: [SourceFinderPanelComponent, researchTranslations(lang)], providers: [...ZONELESS_TEST_PROVIDERS] }).compileComponents();
  const fixture = TestBed.createComponent(SourceFinderPanelComponent);
  for (const [name, value] of Object.entries(inputs)) fixture.componentRef.setInput(name, value);
  await fixture.whenStable();
  return { fixture, component: fixture.componentInstance, root: fixture.nativeElement as HTMLElement, copy: researchCopies[lang].sources.find };
}
afterEach(() => TestBed.resetTestingModule());

describe('SourceFinderPanelComponent', () => {
  it.each(['vi', 'en'] as const)('labels the idle panel and optional focus field in %s without claiming an empty run', async lang => {
    const { root, copy } = await setup(lang);
    const section = root.querySelector('section')!;
    expect(root.querySelector('#' + section.getAttribute('aria-labelledby'))?.textContent).toBe(copy.title);
    expect(root.querySelector('label')?.textContent).toBe(copy.queryLabel);
    expect(root.querySelector('input')?.maxLength).toBe(300);
    expect(root.textContent).toContain(copy.note);
    expect(root.querySelector('nts-empty-state')).toBeNull();
    expect(root.querySelector('input[type=checkbox], input[type=radio], [aria-selected=true], [aria-checked=true]')).toBeNull();
    expect(root.textContent).not.toMatch(/(?:episodes|errors)\./);
  });
  it('emits one trimmed query per submit and null for an empty query', async () => {
    const { root, component, fixture } = await setup();
    const emitted = vi.spyOn(component.find, 'emit');
    const input = root.querySelector('input')!;
    input.value = '  public record  '; input.dispatchEvent(new Event('input')); await fixture.whenStable();
    root.querySelector<HTMLButtonElement>('button[type=submit]')!.click();
    expect(emitted.mock.calls).toEqual([['public record']]);
    input.value = '   '; input.dispatchEvent(new Event('input')); await fixture.whenStable();
    root.querySelector<HTMLButtonElement>('button[type=submit]')!.click();
    expect(emitted.mock.calls).toEqual([['public record'], [null]]);
  });
  it('rejects an overlength programmatic value and guards a running submit', async () => {
    const { root, component, fixture } = await setup();
    const emitted = vi.spyOn(component.find, 'emit');
    component.query.setValue('a'.repeat(301)); component.submit(); expect(emitted).not.toHaveBeenCalled();
    fixture.componentRef.setInput('running', true); await fixture.whenStable(); component.submit();
    expect(emitted).not.toHaveBeenCalled();
    expect(root.querySelector<HTMLButtonElement>('button[type=submit]')!.disabled).toBe(true);
  });
  it.each(['vi', 'en'] as const)('announces a running search as text in %s and marks its disabled action busy', async lang => {
    const { root, copy } = await setup(lang, { running: true });
    expect(root.querySelector('[aria-live=polite]')?.textContent).toContain(copy.running);
    const button = root.querySelector<HTMLButtonElement>('button[type=submit]')!;
    expect(button.disabled).toBe(true); expect(button.getAttribute('aria-busy')).toBe('true');
  });
  it('renders server order, titles, sites, safe new-tab links and explicit Add/Dismiss actions', async () => {
    const rows = [candidate({ id: 'second-ranked', rank: 2 }), candidate({ id: 'first-ranked', rank: 1, siteName: 'dantri.com.vn' })];
    const { root, component } = await setup('en', { candidates: rows });
    const added = vi.spyOn(component.add, 'emit'); const dismissed = vi.spyOn(component.dismiss, 'emit');
    const list = [...root.querySelectorAll('li')]; expect(list).toHaveLength(2);
    expect(list[0]?.textContent).toContain('Public record'); expect(list[0]?.textContent).toContain('vnexpress.net');
    const link = list[0]!.querySelector('a')!;
    expect(link.getAttribute('href')).toBe(rows[0]!.url); expect(link.target).toBe('_blank'); expect(link.rel).toBe('noopener noreferrer');
    expect(link.getAttribute('aria-label')).toBe('Open source from vnexpress.net (opens in a new tab)');
    const buttons = list[0]!.querySelectorAll('button');
    expect(buttons[0]?.getAttribute('aria-label')).toBe('Add suggestion from vnexpress.net');
    expect(buttons[1]?.getAttribute('aria-label')).toBe('Dismiss suggestion from vnexpress.net');
    expect(added).not.toHaveBeenCalled(); expect(dismissed).not.toHaveBeenCalled();
    buttons[0]!.click(); buttons[1]!.click(); expect(added.mock.calls).toEqual([['second-ranked']]); expect(dismissed.mock.calls).toEqual([['second-ranked']]);
    expect(root.querySelector('input[type=checkbox], input[type=radio]')).toBeNull();
  });
  it('keeps busy-row focus and suppresses its actions without disabling another row', async () => {
    const { root, component, fixture } = await setup('en', { candidates: [candidate(), candidate({ id: 'candidate-b' })] });
    const added = vi.spyOn(component.add, 'emit'); const dismissed = vi.spyOn(component.dismiss, 'emit');
    const first = root.querySelector<HTMLButtonElement>('li button')!; first.focus();
    fixture.componentRef.setInput('busyId', 'candidate-a'); await fixture.whenStable();
    expect(document.activeElement).toBe(first);
    const rows = root.querySelectorAll('li');
    expect(rows[0]!.querySelector('button')?.getAttribute('aria-disabled')).toBe('true');
    expect(rows[1]!.querySelector('button')?.getAttribute('aria-disabled')).toBe('false');
    rows[0]!.querySelectorAll('button').forEach(button => button.click()); expect(added).not.toHaveBeenCalled(); expect(dismissed).not.toHaveBeenCalled();
    rows[1]!.querySelector('button')!.click(); expect(added.mock.calls).toEqual([['candidate-b']]); expect(dismissed).not.toHaveBeenCalled();
  });
  it('falls back to a raw long URL and leaves hostile title text inert', async () => {
    const url = 'https://vnexpress.net/' + 'long-path-'.repeat(80);
    const title = '<img onerror="alert(1)" src=x>';
    const { root } = await setup('en', { candidates: [candidate({ title: null, url }), candidate({ id: 'hostile', title })] });
    const rows = root.querySelectorAll('li');
    expect(rows[0]!.querySelector('strong')?.textContent).toBe(url); expect(rows[0]!.querySelector('strong')?.title).toBe(url);
    expect(rows[0]!.querySelector('a')?.getAttribute('href')).toBe(url);
    expect(rows[1]!.querySelector('strong')?.textContent).toBe(title); expect(root.querySelector('img')).toBeNull();
  });
  it.each(['', '   ', '\t\n'])('falls back to the URL for an empty or whitespace-only title (%j)', async title => {
    const item = candidate({ title });
    const { root } = await setup('en', { candidates: [item] });
    const heading = root.querySelector<HTMLElement>('li strong')!;
    expect(heading.textContent).toBe(item.url); expect(heading.title).toBe(item.url);
    expect(root.querySelector('li a')?.getAttribute('href')).toBe(item.url);
  });
  it('shows added and dismissed text without row actions', async () => {
    const { root, copy } = await setup('en', { candidates: [candidate({ status: 'added' }), candidate({ id: 'dismissed', status: 'dismissed' })] });
    expect(root.querySelectorAll('li button')).toHaveLength(0); expect(root.textContent).toContain(copy.added); expect(root.textContent).toContain(copy.dismissed);
  });
  it.each(['added', 'dismissed'] as const)('keeps a completed %s row visible without showing an empty result or allowing Add', async status => {
    const item = candidate({ status });
    const { root, component, copy } = await setup('en', { completed: true, candidates: [item] });
    const added = vi.spyOn(component.add, 'emit');
    expect(root.querySelector('nts-empty-state')).toBeNull();
    expect(root.querySelectorAll('li')).toHaveLength(1);
    expect(root.querySelector('li')?.textContent).toContain(status === 'added' ? copy.added : copy.dismissed);
    expect(root.querySelector('li button')).toBeNull();
    component.addCandidate(item.id); expect(added).not.toHaveBeenCalled();
  });
  it('emits once for two rapid Add clicks when the first click marks the row busy', async () => {
    const { root, component, fixture } = await setup('en', { candidates: [candidate()] });
    const added = vi.spyOn(component.add, 'emit').mockImplementation(id => fixture.componentRef.setInput('busyId', id));
    const button = root.querySelector<HTMLButtonElement>('li button')!;
    button.click(); button.click();
    expect(added.mock.calls).toEqual([['candidate-a']]);
    await fixture.whenStable(); expect(button.getAttribute('aria-disabled')).toBe('true');
  });
  it('shows zero candidates as a successful empty state only after completion', async () => {
    const { root, copy } = await setup('en', { completed: true }); expect(root.querySelector('nts-empty-state')?.textContent).toContain(copy.empty); expect(root.querySelector('[role=alert]')).toBeNull();
  });
  it('renders failed-run recovery and emits a fresh user retry action', async () => {
    const { root, component, copy } = await setup('en', { failedRun: true });
    expect(root.querySelector('[role=alert]')?.textContent).toContain(researchCopies.en.sources.error.find_sources_generation_failed);
    const emitted = vi.spyOn(component.find, 'emit');
    const button = root.querySelector<HTMLButtonElement>('button[type=submit]')!;
    expect(button.textContent).toContain(copy.run); expect(button.disabled).toBe(false); button.click(); expect(emitted.mock.calls).toEqual([[null]]);
  });
  it('renders panel and row errors in their own alerts, points duplicates to sources and blocks rate-limited find', async () => {
    const { root, copy } = await setup('en', { candidates: [candidate()], errorKey: 'errors.spend_cap_reached', retryAfterSec: 30, rowErrorKeys: new Map([['candidate-a', 'errors.source_duplicate']]) });
    expect(root.querySelectorAll('[role=alert]')).toHaveLength(2);
    expect(root.querySelector('li [role=alert]')?.textContent).toContain(copy.rowAlreadyInList);
    expect(root.querySelector('nts-retry-countdown')).not.toBeNull();
    expect(root.querySelector<HTMLButtonElement>('button[type=submit]')!.disabled).toBe(true);
    expect(root.textContent).not.toMatch(/\b(?:credits?|price|balance)\b|\$|₫/i);
  });
  it('uses one row countdown for source-fetch limits and emits its elapsed event', async () => {
    const { root, component, fixture } = await setup('en', { candidates: [candidate()], rowErrorKeys: new Map([['candidate-a', 'errors.source_fetch_limit_reached']]), rowRetryAfterSec: 30 });
    const elapsed = vi.spyOn(component.rowRetryElapsed, 'emit');
    expect(root.querySelector('li nts-retry-countdown')).not.toBeNull();
    expect(root.querySelector('li button')?.getAttribute('aria-disabled')).toBe('true');
    fixture.componentRef.setInput('rowRetryAfterSec', 0); await fixture.whenStable(); expect(elapsed).toHaveBeenCalledTimes(1);
  });
  it('loads suggestions with the shared loading state instead of flashing empty', async () => {
    const { root, copy } = await setup('en', { loading: true, completed: true });
    expect(root.querySelector('nts-loading-state')?.textContent).toContain(copy.loading);
    expect(root.querySelector('nts-empty-state')).toBeNull();
  });
  it('points a full episode to its sources list and leaves dismiss available', async () => {
    const { root, component, copy } = await setup('en', { candidates: [candidate()], sourceLimitReached: true });
    const added = vi.spyOn(component.add, 'emit'); const dismissed = vi.spyOn(component.dismiss, 'emit');
    const buttons = root.querySelectorAll<HTMLButtonElement>('li button');
    buttons[0]!.click(); buttons[1]!.click();
    expect(added).not.toHaveBeenCalled(); expect(dismissed.mock.calls).toEqual([['candidate-a']]); expect(root.textContent).toContain(copy.sourceLimit);
  });
  it('forwards one edit notification and one panel retry-elapsed notification', async () => {
    const { root, component, fixture } = await setup('en', { retryAfterSec: 30 });
    const edited = vi.spyOn(component.clearError, 'emit'); const elapsed = vi.spyOn(component.retryElapsed, 'emit');
    const input = root.querySelector('input')!; input.value = 'new focus'; input.dispatchEvent(new Event('input'));
    expect(edited).toHaveBeenCalledTimes(1);
    fixture.componentRef.setInput('retryAfterSec', 0); await fixture.whenStable(); expect(elapsed).toHaveBeenCalledTimes(1);
  });
});
