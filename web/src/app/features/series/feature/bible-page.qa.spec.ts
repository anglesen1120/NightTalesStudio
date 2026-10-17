// QA (10.5.7-T1) — form boundaries (every documented limit at its exact edge), the 30-entry add guard, the
// pre-request rejection of angle brackets in names, the exact save payload for a bible carrying server-derived
// members, indexed server field errors, an accept after a 412 and a double accept, and XSS strings rendered as text.
import { DeferBlockBehavior, TestBed } from '@angular/core/testing';
import { ActivatedRoute, convertToParamMap, provideRouter } from '@angular/router';
import { TranslocoTestingModule } from '@jsverse/transloco';
import type { AbstractControl } from '@angular/forms';
import { Subject, of } from 'rxjs';
import type { Observable } from 'rxjs';
import { afterEach, describe, expect, it, vi } from 'vitest';
import coreEn from '../../../../assets/i18n/core/en.json';
import coreVi from '../../../../assets/i18n/core/vi.json';
import seriesEn from '../../../../assets/i18n/series/en.json';
import seriesVi from '../../../../assets/i18n/series/vi.json';
import errorsEn from '../../../../assets/i18n/errors/en.json';
import errorsVi from '../../../../assets/i18n/errors/vi.json';
import { ConfirmDialog } from '@shared/ui';
import { ZONELESS_TEST_PROVIDERS } from '../../../testing/providers';
import { BibleRepository } from '../application/bible.repository';
import {
  BIBLE_CONFLICT,
  BIBLE_PROPOSAL_GONE,
  BIBLE_SERIES_ID,
  FakeBibleRepository,
  bible,
  bibleContent,
  bibleProposal,
} from '../testing/fake-bible.repository';
import type { BibleContent } from '../domain/bible.model';
import { BiblePage } from './bible-page';

afterEach(() => TestBed.resetTestingModule());

async function setup(repo = new FakeBibleRepository(), lang: 'vi' | 'en' = 'en', confirmation: Observable<boolean> = of(true)) {
  const params = convertToParamMap({ id: BIBLE_SERIES_ID });
  const confirm = vi.fn(() => ({ afterClosed: () => confirmation }));
  await TestBed.configureTestingModule({
    imports: [BiblePage, TranslocoTestingModule.forRoot({ langs: { en: { ...coreEn, series: seriesEn, errors: errorsEn }, vi: { ...coreVi, series: seriesVi, errors: errorsVi }, 'series/en': seriesEn, 'series/vi': seriesVi, 'errors/en': errorsEn, 'errors/vi': errorsVi }, translocoConfig: { availableLangs: ['vi', 'en'], defaultLang: lang, fallbackLang: lang, reRenderOnLangChange: true }, preloadLangs: true })],
    providers: [...ZONELESS_TEST_PROVIDERS, provideRouter([]), { provide: BibleRepository, useValue: repo }, { provide: ActivatedRoute, useValue: { paramMap: of(params), snapshot: { paramMap: params } } }, { provide: ConfirmDialog, useValue: { confirm } }],
    deferBlockBehavior: DeferBlockBehavior.Manual,
  }).compileComponents();
  const fixture = TestBed.createComponent(BiblePage);
  await fixture.whenStable();
  await expect.poll(() => repo.getCalls.length === 1 && !fixture.componentInstance.store.loading()).toBe(true);
  await fixture.whenStable();
  return { fixture, component: fixture.componentInstance, repo, root: fixture.nativeElement as HTMLElement, confirm };
}

/** `Promise.withResolvers` is not in this project's `lib` (ES2022); the same one-shot gate as the store spec. */
function deferred<T>(): { readonly promise: Promise<T>; resolve(value: T): void } {
  let resolve!: (value: T) => void;
  const promise = new Promise<T>((settle) => {
    resolve = settle;
  });
  return { promise, resolve };
}

/** The dirty signal is debounced by 150 ms; wait past it before reading `canSave()` (ES2022: no `withResolvers`). */
async function settleDirty(fixture: { whenStable(): Promise<unknown> }): Promise<void> {
  await new Promise<void>((resolve) => setTimeout(resolve, 170));
  await fixture.whenStable();
}

function controlOf(component: BiblePage, path: string): AbstractControl {
  const control = component.form()?.get(path);
  if (!control) throw new Error(`missing form control ${path}`);
  return control;
}

describe('BiblePage (QA form boundaries)', () => {
  it.each([
    ['narrator.name', 80, true],
    ['narrator.name', 81, false],
    ['narrator.persona', 1_000, true],
    ['narrator.persona', 1_001, false],
    ['narrator.voiceNotes', 1_000, true],
    ['narrator.voiceNotes', 1_001, false],
    ['recurringCharacters.0.name', 60, true],
    ['recurringCharacters.0.name', 61, false],
    ['recurringCharacters.0.description', 300, true],
    ['recurringCharacters.0.description', 301, false],
    ['places.0.name', 60, true],
    ['places.0.name', 61, false],
    ['rules.0.text', 300, true],
    ['rules.0.text', 301, false],
    ['episodeSummaries.0.title', 100, true],
    ['episodeSummaries.0.title', 101, false],
    ['episodeSummaries.0.summary', 200, true],
    ['episodeSummaries.0.summary', 201, false],
  ])('%s accepts %i characters: %s (Save gating follows)', async (path, length, accepted) => {
    const { fixture, component } = await setup();
    controlOf(component, path).setValue('a'.repeat(length));
    await settleDirty(fixture);
    expect(controlOf(component, path).valid).toBe(accepted);
    expect(component.canSave()).toBe(accepted);
  });

  it('renders the documented maxlength on every input and counter denominator', async () => {
    const { root } = await setup();
    expect(root.querySelector<HTMLInputElement>('fieldset.narrator input')?.getAttribute('maxlength')).toBe('80');
    const narratorTextareas = root.querySelectorAll<HTMLTextAreaElement>('fieldset.narrator textarea');
    expect([...narratorTextareas].map((node) => node.getAttribute('maxlength'))).toEqual(['1000', '1000']);
    const sections = root.querySelectorAll('nts-bible-section');
    expect(sections[0]!.querySelector<HTMLInputElement>('.entry input')?.getAttribute('maxlength')).toBe('60');
    expect(sections[0]!.querySelector<HTMLTextAreaElement>('.entry textarea')?.getAttribute('maxlength')).toBe('300');
    expect(sections[2]!.querySelector<HTMLTextAreaElement>('.entry textarea')?.getAttribute('maxlength')).toBe('300');
    expect(sections[3]!.querySelector<HTMLInputElement>('.entry input')?.getAttribute('maxlength')).toBe('100');
    expect(sections[3]!.querySelector<HTMLTextAreaElement>('.entry textarea')?.getAttribute('maxlength')).toBe('200');
    for (const denominator of ['/80', '/1,000', '/60', '/300', '/100', '/200']) expect(root.textContent).toContain(denominator);
  });

  it('stops adding rows at 30 entries per array', async () => {
    const { component } = await setup();
    for (let index = 1; index < 30; index += 1) component.addEntry('characters');
    expect(component.form()!.controls.recurringCharacters.length).toBe(30);
    component.addEntry('characters');
    expect(component.form()!.controls.recurringCharacters.length).toBe(30);
  });

  it.each(['narrator.name', 'recurringCharacters.0.name', 'places.0.name'])(
    'rejects < and > in %s before any request',
    async (path) => {
      const { fixture, component, repo } = await setup();
      for (const value of ['<b>', 'a > b', '<img src=x onerror=alert(1)>']) {
        controlOf(component, path).setValue(value);
        await settleDirty(fixture);
        expect(controlOf(component, path).hasError('angleBrackets')).toBe(true);
        expect(component.canSave()).toBe(false);
        await component.save();
      }
      expect(repo.saveCalls).toHaveLength(0);
    },
  );

  it('sends the server-derived members only where the contract keeps them and echoes the server ids', async () => {
    const { fixture, component, repo } = await setup();
    component.addEntry('characters');
    const root = component.form()!;
    root.controls.recurringCharacters.at(1).controls.name!.setValue('Ông Tư');
    root.controls.recurringCharacters.at(1).controls.description!.setValue('Hàng xóm');
    root.controls.episodeSummaries.at(0).controls.title!.setValue('Tập một đã sửa');
    await settleDirty(fixture);

    repo.scriptSave(() => {
      repo.getResult = {
        ok: true,
        value: bible({
          version: 2,
          etag: '"sb-2"',
          content: bibleContent({
            recurringCharacters: [
              { id: 'ch1', name: 'Bà Hoa', description: 'Người gác cổng khu chung cư', firstEpisodeId: 'ep-1' },
              { id: 'ch7', name: 'Ông Tư', description: 'Hàng xóm' },
            ],
          }),
        }),
      };
      return Promise.resolve(repo.saveResult);
    });
    await component.save();
    await fixture.whenStable();
    await expect.poll(() => repo.getCalls.length).toBe(2);
    await fixture.whenStable();

    const content: BibleContent = repo.saveCalls[0]!.content;
    expect(repo.saveCalls[0]!.etag).toBe('"sb-1"');
    expect(content.recurringCharacters[0]).toEqual({ id: 'ch1', name: 'Bà Hoa', description: 'Người gác cổng khu chung cư', firstEpisodeId: 'ep-1' });
    expect(content.recurringCharacters[1]).toEqual({ name: 'Ông Tư', description: 'Hàng xóm' });
    expect(content.episodeSummaries[0]).toEqual({ episodeId: 'ep-1', title: 'Tập một đã sửa', summary: bibleContent().episodeSummaries[0]!.summary });
    expect('structure' in content.episodeSummaries[0]!).toBe(false);
    expect('approvedAt' in content.episodeSummaries[0]!).toBe(false);

    // The refetch assigns the id and the rebuilt form carries it, while the existing row keeps its own.
    expect(component.form()!.controls.recurringCharacters.at(1).controls.id?.value).toBe('ch7');
    expect(component.form()!.controls.recurringCharacters.at(0).controls.id?.value).toBe('ch1');
    expect(component.form()!.controls.recurringCharacters.at(1).controls.firstEpisodeId?.value ?? '').toBe('');
  });

  it('binds an indexed server field error to its row control instead of the form-level alert', async () => {
    const repo = new FakeBibleRepository();
    repo.getResult = {
      ok: true,
      value: bible({
        content: bibleContent({
          episodeSummaries: [
            { episodeId: 'ep-1', title: 'Một', summary: 'Một' },
            { episodeId: 'ep-2', title: 'Hai', summary: 'Hai' },
            { episodeId: 'ep-3', title: 'Ba', summary: 'Ba' },
          ],
        }),
      }),
    };
    const { fixture, component } = await setup(repo);
    component.form()!.controls.episodeSummaries.at(2).controls.summary!.setValue('Ba (sửa)');
    await settleDirty(fixture);
    repo.saveResult = {
      ok: false,
      error: {
        code: 'bible_invalid_content',
        status: 400,
        kind: 'validation',
        fieldErrors: { 'episode_summaries[2].episode_id': ['unknown_episode'], 'rules[0].text': ['too_long'] },
      },
    };
    await component.save();
    await fixture.whenStable();

    const row = component.form()!.controls.episodeSummaries.at(2);
    expect(row.controls.episodeId!.hasError('server')).toBe(true);
    expect(component.form()!.controls.rules.at(0).controls.text!.hasError('server')).toBe(true);
    expect(component.unmappedKeys()).toEqual([]);
    expect(component.fieldIssues().map((issue) => issue.pointer)).toEqual(['episodeSummaries.2.episodeId', 'rules.0.text']);
    expect(component.fieldIssues().map((issue) => issue.messageKey)).toEqual(['series.bible.field.unknown_episode', 'series.bible.field.too_long']);
  });

  it('treats a rule text containing < as a server decision, not a client block (documented gap)', async () => {
    const { fixture, component, repo } = await setup();
    controlOf(component, 'rules.0.text').setValue('Không dùng <biệt danh> của phản diện');
    await settleDirty(fixture);
    expect(controlOf(component, 'rules.0.text').valid).toBe(true);
    expect(component.canSave()).toBe(true);
    await component.save();
    expect(repo.saveCalls[0]!.content.rules[0]!.text).toBe('Không dùng <biệt danh> của phản diện');
  });

  it('renders an XSS-shaped description, title and summary as control values only', async () => {
    const { fixture, component, root } = await setup();
    const payload = '<img src=x onerror="globalThis.__bibleXss = 1">';
    controlOf(component, 'recurringCharacters.0.description').setValue(payload);
    controlOf(component, 'episodeSummaries.0.title').setValue(payload);
    controlOf(component, 'episodeSummaries.0.summary').setValue(payload);
    await settleDirty(fixture);
    await fixture.whenStable();

    expect(root.querySelector('img')).toBeNull();
    const narratorInput = root.querySelector<HTMLInputElement>('fieldset.narrator input');
    expect(narratorInput?.value).not.toContain('onerror');
    const summaries = root.querySelectorAll('nts-bible-section')[3]!;
    expect(summaries.querySelector<HTMLInputElement>('.entry input')?.value).toBe(payload);
    expect('__bibleXss' in globalThis).toBe(false);
  });

  it('blocks Save on a fresh bible with an empty persona and shows the required hint', async () => {
    const repo = new FakeBibleRepository();
    repo.getResult = { ok: true, value: bible({ content: bibleContent({ narrator: { name: '', persona: '', voiceNotes: '' } }) }) };
    const { component, root } = await setup(repo);
    expect(component.canSave()).toBe(false);
    expect(component.form()!.controls.narrator.controls.persona.hasError('required')).toBe(true);
    expect(root.querySelector('#persona-required')?.textContent).toContain(seriesEn.bible.field.required);
  });

  it('keeps the edit and conflict after a 412 when the accept discard confirmation is cancelled', async () => {
    const repo = new FakeBibleRepository();
    repo.getResult = { ok: true, value: bible({ proposal: bibleProposal() }) };
    const { fixture, component, repo: fake, confirm } = await setup(repo, 'en', of(false));
    controlOf(component, 'recurringCharacters.0.name').setValue('Bản chưa lưu');
    await settleDirty(fixture);
    fake.saveResult = { ok: false, error: BIBLE_CONFLICT };
    await component.save();
    await fixture.whenStable();
    expect(component.store.conflict()).toBe(true);
    expect(component.canSave()).toBe(false);

    await component.acceptProposal();
    await fixture.whenStable();

    expect(confirm).toHaveBeenCalledOnce();
    expect(fake.acceptCalls).toEqual([]);
    expect(component.store.conflict()).toBe(true);
    expect(component.store.proposal()).not.toBeNull();
    expect(controlOf(component, 'recurringCharacters.0.name').value).toBe('Bản chưa lưu');
    expect(component.dirty()).toBe(true);
  });

  it('keeps edits and dirty state after dismiss refetches unchanged content', async () => {
    const repo = new FakeBibleRepository();
    repo.getResult = { ok: true, value: bible({ proposal: bibleProposal() }) };
    const { component, fixture, root, confirm } = await setup(repo);
    const input = root.querySelector<HTMLInputElement>('fieldset.narrator input')!;
    input.value = 'Unsaved narrator';
    input.dispatchEvent(new Event('input'));
    await settleDirty(fixture);
    repo.getResult = { ok: true, value: bible({ content: bibleContent({ narrator: { persona: bibleContent().narrator.persona, voiceNotes: bibleContent().narrator.voiceNotes, name: bibleContent().narrator.name } }) }) };

    await component.dismissProposal();
    await fixture.whenStable();

    expect(repo.dismissCalls).toEqual([BIBLE_SERIES_ID]);
    expect(repo.getCalls).toHaveLength(2);
    expect(controlOf(component, 'narrator.name').value).toBe('Unsaved narrator');
    expect(component.dirty()).toBe(true);
    expect(component.form()?.dirty).toBe(true);
    expect(component.store.proposal()).toBeNull();
    expect(confirm).not.toHaveBeenCalled();
  });

  it('keeps edits after a confirmed accept returns 409 and refetches unchanged content', async () => {
    const repo = new FakeBibleRepository();
    repo.getResult = { ok: true, value: bible({ proposal: bibleProposal() }) };
    const { component, fixture, confirm } = await setup(repo);
    controlOf(component, 'narrator.name').setValue('Unsaved narrator');
    await settleDirty(fixture);
    repo.acceptResult = { ok: false, error: BIBLE_PROPOSAL_GONE };
    repo.getResult = { ok: true, value: bible() };

    await component.acceptProposal();
    await fixture.whenStable();

    expect(confirm).toHaveBeenCalledOnce();
    expect(repo.acceptCalls).toEqual([BIBLE_SERIES_ID]);
    expect(repo.getCalls).toHaveLength(2);
    expect(controlOf(component, 'narrator.name').value).toBe('Unsaved narrator');
    expect(component.dirty()).toBe(true);
    expect(component.store.proposalGone()).toBe(true);
  });

  it.each(['vi', 'en'] as const)('waits for explicit discard confirmation before dirty accept in %s', async lang => {
    const repo = new FakeBibleRepository();
    repo.getResult = { ok: true, value: bible({ proposal: bibleProposal() }) };
    const confirmation = new Subject<boolean>();
    const { component, fixture, root, confirm } = await setup(repo, lang, confirmation);
    const input = root.querySelector<HTMLInputElement>('fieldset.narrator input')!;
    input.value = 'Unsaved narrator';
    input.dispatchEvent(new Event('input'));
    // Accept is protected even before the dirty debounce has fired.
    const accepting = component.acceptProposal();
    expect(confirm).toHaveBeenCalledOnce();
    expect(repo.acceptCalls).toEqual([]);
    expect(controlOf(component, 'narrator.name').value).toBe('Unsaved narrator');
    await component.acceptProposal();
    expect(confirm).toHaveBeenCalledOnce();
    repo.getResult = { ok: true, value: bible({ version: 2, etag: '"sb-2"', content: bibleContent({ rules: [{ id: 'rl1', text: 'Accepted rule' }] }) }) };

    confirmation.next(true);
    confirmation.complete();
    await accepting;
    await fixture.whenStable();

    expect(repo.acceptCalls).toEqual([BIBLE_SERIES_ID]);
    expect(controlOf(component, 'narrator.name').value).toBe(bibleContent().narrator.name);
    expect(controlOf(component, 'rules.0.text').value).toBe('Accepted rule');
    expect(component.dirty()).toBe(false);
  });

  it('accepts a clean form without asking to discard edits and adopts the appended content', async () => {
    const repo = new FakeBibleRepository();
    repo.getResult = { ok: true, value: bible({ proposal: bibleProposal() }) };
    const { component, fixture, confirm } = await setup(repo);
    repo.getResult = { ok: true, value: bible({ version: 2, etag: '"sb-2"', content: bibleContent({ rules: [...bibleContent().rules, { id: 'rl2', text: 'Accepted rule' }] }) }) };

    await component.acceptProposal();
    await fixture.whenStable();

    expect(confirm).not.toHaveBeenCalled();
    expect(repo.acceptCalls).toEqual([BIBLE_SERIES_ID]);
    expect(component.form()?.controls.rules.length).toBe(2);
    expect(controlOf(component, 'rules.1.text').value).toBe('Accepted rule');
    expect(component.store.bible()?.version).toBe(2);
    expect(component.store.proposal()).toBeNull();
  });

  it('explicit Reload discards local edits even when server content is unchanged', async () => {
    const { component, fixture } = await setup();
    controlOf(component, 'narrator.name').setValue('Unsaved narrator');
    await settleDirty(fixture);

    await component.reload();
    await fixture.whenStable();

    expect(controlOf(component, 'narrator.name').value).toBe(bibleContent().narrator.name);
    expect(component.dirty()).toBe(false);
  });

  it('a second accept while the first is committing issues one request and disables the buttons', async () => {
    const repo = new FakeBibleRepository();
    repo.getResult = { ok: true, value: bible({ proposal: bibleProposal() }) };
    const gate = deferred<{ ok: true; value: undefined }>();
    repo.scriptAccept(() => gate.promise);
    const { fixture, component, root } = await setup(repo);

    const first = component.acceptProposal();
    await Promise.resolve();
    await fixture.whenStable();
    const buttons = root.querySelectorAll<HTMLButtonElement>('nts-bible-proposal-banner .proposal__actions button');
    expect([...buttons].map((button) => button.disabled)).toEqual([true, true]);
    await component.acceptProposal();

    expect(repo.acceptCalls).toHaveLength(1);
    gate.resolve({ ok: true, value: undefined });
    await first;
    await fixture.whenStable();
    expect(component.store.committing()).toBe(false);
  });

  it('shows the no-title banner copy for a proposal without a proposing episode and asks for no title', async () => {
    const repo = new FakeBibleRepository();
    repo.getResult = { ok: true, value: bible({ proposal: bibleProposal({ proposedFromEpisodeId: null, ops: [{ kind: 'AddCharacter', name: 'Ông Tư' }] }) }) };
    const { root } = await setup(repo);
    expect(repo.titleCalls).toEqual([]);
    expect(root.querySelector('nts-bible-proposal-banner h2')?.textContent).toBe(seriesEn.bible.proposal.bannerNoTitle.replace('{{n}}', '1'));
  });

  it('surfaces a 428 as the mapped error copy and keeps the form editable', async () => {
    const { fixture, component, repo, root } = await setup();
    controlOf(component, 'narrator.name').setValue('Người kể mới');
    await settleDirty(fixture);
    repo.saveResult = { ok: false, error: { code: 'if_match_required', status: 428, kind: 'precondition', fieldErrors: {} } };
    await component.save();
    await fixture.whenStable();
    expect(component.store.error()?.code).toBe('if_match_required');
    expect(root.querySelector('nts-form-error-alert')?.textContent).toContain(errorsEn.if_match_required);
    expect(component.canSave()).toBe(true);
  });
});
