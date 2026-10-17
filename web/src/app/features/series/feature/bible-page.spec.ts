import { DeferBlockBehavior, TestBed } from '@angular/core/testing';
import { ActivatedRoute, convertToParamMap, provideRouter } from '@angular/router';
import { TranslocoTestingModule } from '@jsverse/transloco';
import { of } from 'rxjs';
import { afterEach, describe, expect, it } from 'vitest';
import coreEn from '../../../../assets/i18n/core/en.json';
import coreVi from '../../../../assets/i18n/core/vi.json';
import seriesEn from '../../../../assets/i18n/series/en.json';
import seriesVi from '../../../../assets/i18n/series/vi.json';
import errorsEn from '../../../../assets/i18n/errors/en.json';
import errorsVi from '../../../../assets/i18n/errors/vi.json';
import { UnsavedWorkRegistry } from '@core/shell/unsaved-work';
import { ConfirmDialog } from '@shared/ui';
import { ZONELESS_TEST_PROVIDERS } from '../../../testing/providers';
import { BibleRepository } from '../application/bible.repository';
import { FakeBibleRepository, BIBLE_CONFLICT, BIBLE_INVALID, BIBLE_NOT_FOUND, BIBLE_PROPOSAL_GONE, BIBLE_SERIES_ID, bible, bibleContent, bibleProposal } from '../testing/fake-bible.repository';
import { BiblePage } from './bible-page';

afterEach(() => TestBed.resetTestingModule());
async function setup(repo = new FakeBibleRepository(), lang: 'vi' | 'en' = 'en') {
  const params = convertToParamMap({ id: BIBLE_SERIES_ID });
  await TestBed.configureTestingModule({
    imports: [BiblePage, TranslocoTestingModule.forRoot({ langs: { en: { ...coreEn, series: seriesEn, errors: errorsEn }, vi: { ...coreVi, series: seriesVi, errors: errorsVi }, 'series/en': seriesEn, 'series/vi': seriesVi, 'errors/en': errorsEn, 'errors/vi': errorsVi }, translocoConfig: { availableLangs: ['vi', 'en'], defaultLang: lang, fallbackLang: lang, reRenderOnLangChange: true }, preloadLangs: true })],
    providers: [...ZONELESS_TEST_PROVIDERS, provideRouter([]), { provide: BibleRepository, useValue: repo }, { provide: ActivatedRoute, useValue: { paramMap: of(params), snapshot: { paramMap: params } } }, { provide: ConfirmDialog, useValue: { confirm: () => ({ afterClosed: () => of(true) }) } }],
    deferBlockBehavior: DeferBlockBehavior.Manual,
  }).compileComponents();
  const fixture = TestBed.createComponent(BiblePage);
  await fixture.whenStable();
  // A promise started by an effect is not a zoneless pending task: wait for the repository result explicitly.
  await expect.poll(() => repo.getCalls.length === 1 && !fixture.componentInstance.store.loading()).toBe(true);
  await fixture.whenStable();
  return { fixture, component: fixture.componentInstance, repo, root: fixture.nativeElement as HTMLElement };
}
async function settleDirty(): Promise<void> { await new Promise<void>(resolve => setTimeout(resolve, 170)); }

describe('BiblePage', () => {
  it.each(['vi', 'en'] as const)('loads once and renders all five sections in %s', async lang => {
    const { repo, root, component } = await setup(new FakeBibleRepository(), lang);
    expect(repo.getCalls).toEqual([BIBLE_SERIES_ID]); expect(repo.titleCalls).toEqual([]);
    expect(root.querySelectorAll('fieldset')).toHaveLength(5);
    expect(Object.keys(component.form()!.controls.narrator.controls)).toEqual(['name', 'persona', 'voiceNotes']);
    expect(component.canSave()).toBe(false);
  });
  it('debounces dirty gating and saves the full content with the loaded ETag', async () => {
    const { fixture, component, repo, root } = await setup();
    const input = root.querySelector<HTMLInputElement>('fieldset.narrator input')!;
    input.value = 'New narrator'; input.dispatchEvent(new Event('input'));
    expect(component.dirty()).toBe(false);
    await settleDirty(); await fixture.whenStable();
    expect(component.canSave()).toBe(true);
    repo.scriptSave(() => {
      repo.getResult = { ok: true, value: bible({ version: 2, etag: '"sb-2"', content: repo.saveCalls[0]!.content }) };
      return Promise.resolve(repo.saveResult);
    });
    await component.save(); await fixture.whenStable();
    expect(repo.saveCalls).toHaveLength(1);
    expect(repo.saveCalls[0]?.etag).toBe('"sb-1"');
    expect(repo.saveCalls[0]?.content.narrator.name).toBe('New narrator');
    expect(component.store.bible()?.version).toBe(2); expect(component.dirty()).toBe(false);
    expect(root.querySelector('.saved[aria-live="polite"]')?.textContent).toContain(seriesEn.bible.saved);
  });
  it('renders an empty bible while keeping every section available', async () => {
    const repo = new FakeBibleRepository();
    repo.getResult = { ok: true, value: bible({ content: bibleContent({ narrator: { name: '', persona: '', voiceNotes: '' }, recurringCharacters: [], places: [], rules: [], episodeSummaries: [] }) }) };
    const { root, component } = await setup(repo);
    expect(root.querySelector('nts-empty-state')).not.toBeNull();
    expect(root.querySelector('nts-error-state')).toBeNull(); expect(root.querySelectorAll('fieldset')).toHaveLength(5);
    component.addEntry('characters');
    expect(component.form()?.controls.recurringCharacters.length).toBe(1);
  });
  it('keeps local edits on 412 and only replaces them on explicit Reload', async () => {
    const { fixture, root, component, repo } = await setup();
    component.form()!.controls.narrator.controls.name.setValue('Local edit');
    await settleDirty(); await fixture.whenStable();
    repo.saveResult = { ok: false, error: BIBLE_CONFLICT };
    await component.save(); await fixture.whenStable();
    expect(root.querySelector('.conflict[role="alert"]')).not.toBeNull();
    expect(component.form()?.controls.narrator.controls.name.value).toBe('Local edit');
    expect(repo.getCalls).toHaveLength(1);
    repo.getResult = { ok: true, value: bible({ content: bibleContent({ narrator: { name: 'Server edit', persona: 'Persona', voiceNotes: '' } }) }) };
    root.querySelector<HTMLButtonElement>('.conflict button')!.click(); await fixture.whenStable();
    expect(repo.getCalls).toHaveLength(2); expect(component.form()?.controls.narrator.controls.name.value).toBe('Server edit');
  });
  it('requires persona and rejects angle brackets in names before requesting a save', async () => {
    const { fixture, component, repo } = await setup();
    component.form()!.controls.narrator.controls.persona.setValue('');
    await settleDirty(); await fixture.whenStable(); await component.save();
    expect(component.canSave()).toBe(false); expect(repo.saveCalls).toHaveLength(0);
    expect(component.form()?.controls.narrator.controls.persona.hasError('required')).toBe(true);
    component.form()!.controls.narrator.controls.persona.setValue('Persona');
    component.form()!.controls.recurringCharacters.at(0).controls.name!.setValue('<b>');
    await settleDirty(); await fixture.whenStable(); await component.save();
    expect(component.form()?.controls.recurringCharacters.at(0).controls.name?.hasError('angleBrackets')).toBe(true);
    expect(repo.saveCalls).toHaveLength(0);
  });
  it('edits/removes summaries without sending derived fields and omits ids for new rows', async () => {
    const { fixture, component, repo } = await setup();
    component.form()!.controls.episodeSummaries.at(0).controls.title!.setValue('Edited title');
    component.addEntry('rules'); component.form()!.controls.rules.at(1).controls.text!.setValue('New rule');
    await settleDirty(); await fixture.whenStable(); await component.save();
    expect(repo.saveCalls[0]?.content.episodeSummaries[0]).toEqual({ episodeId: 'ep-1', title: 'Edited title', summary: bibleContent().episodeSummaries[0]!.summary });
    expect(repo.saveCalls[0]?.content.rules[0]?.id).toBe('rl1'); expect(repo.saveCalls[0]?.content.rules[1]).not.toHaveProperty('id');
    component.removeEntry('summaries', 0); expect(component.form()?.controls.episodeSummaries.length).toBe(0);
  });
  it('binds snake_case field errors and announces unmapped pointers at form level', async () => {
    const { fixture, component, repo, root } = await setup();
    component.form()!.controls.narrator.controls.name.setValue('Changed'); await settleDirty(); await fixture.whenStable();
    repo.saveResult = { ok: false, error: BIBLE_INVALID }; await component.save(); await fixture.whenStable();
    expect(component.form()?.controls.recurringCharacters.hasError('server')).toBe(true);
    expect(component.unmappedKeys()).toContain('episode_summaries[2].episode_id');
    expect(root.querySelector('nts-form-error-alert[role="alert"]')).not.toBeNull();
    expect(root.querySelector('.field-issues[role="alert"]')).not.toBeNull();
  });
  it('shows a 404 error and a route back to the series list', async () => {
    const repo = new FakeBibleRepository(); repo.getResult = { ok: false, error: BIBLE_NOT_FOUND };
    const { root } = await setup(repo);
    expect(root.querySelector('nts-error-state')).not.toBeNull(); expect(root.querySelector('a[href="/series"]')).not.toBeNull();
  });
  it('resolves one proposal title and handles a proposal already gone in another tab', async () => {
    const repo = new FakeBibleRepository(); const proposal = bibleProposal(); repo.getResult = { ok: true, value: bible({ proposal }) };
    const { component, fixture, root } = await setup(repo);
    expect(repo.titleCalls).toEqual([proposal.proposedFromEpisodeId]); expect(root.querySelector('nts-bible-proposal-banner')).not.toBeNull();
    repo.acceptResult = { ok: false, error: BIBLE_PROPOSAL_GONE }; repo.getResult = { ok: true, value: bible() };
    await component.acceptProposal(); await fixture.whenStable();
    expect(component.store.proposalGone()).toBe(true); expect(root.querySelector('nts-bible-proposal-banner')).toBeNull();
    expect(root.textContent).toContain(seriesEn.bible.proposal.none); expect(repo.getCalls).toHaveLength(2);
  });
  it('registers immediate dirty navigation protection and unregisters on destroy', async () => {
    const { root, component, fixture } = await setup(); const registry = TestBed.inject(UnsavedWorkRegistry);
    const input = root.querySelector<HTMLInputElement>('fieldset.narrator input')!;
    input.value = 'Unsaved'; input.dispatchEvent(new Event('input'));
    expect(registry.hasUnsaved()).toBe(true);
    const event = new Event('beforeunload', { cancelable: true }); component.beforeUnload(event); expect(event.defaultPrevented).toBe(true);
    fixture.destroy(); expect(registry.hasUnsaved()).toBe(false); expect(component.store.bible()).toBeNull();
  });
});
