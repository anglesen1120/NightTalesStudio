import { signal } from '@angular/core';
import { DeferBlockBehavior, DeferBlockState, TestBed } from '@angular/core/testing';
import { ActivatedRoute, convertToParamMap, provideRouter } from '@angular/router';
import { of } from 'rxjs';
import { afterEach, describe, expect, it, vi } from 'vitest';
import type { AppError } from '@core/errors';
import { ConfirmDialog } from '@shared/ui';
import { ZONELESS_TEST_PROVIDERS } from '../../../../testing/providers';
import { ResearchStore } from '../../application/research.store';
import type { Fact, FactSheet, ResearchSource, SourceLimits } from '../../domain/research.model';
import { researchTranslations } from '../../testing/research-ui.testing';
import { source } from '../../testing/fake-research.repository';
import { ResearchPageComponent } from './research-page.component';

/** Page-level fake: the store methods the page calls, with recorded arguments. */
class FakeResearchStore {
  readonly sources = signal<readonly ResearchSource[]>([]);
  readonly limits = signal<SourceLimits | null>(null);
  readonly factSheet = signal<FactSheet | null>(null);
  readonly draft = signal<readonly (Fact & { readonly clientKey: string })[]>([]);
  readonly loaded = signal(true);
  readonly loading = signal(false);
  readonly refreshing = signal(false);
  readonly generation = signal(null);
  readonly running = signal(false);
  readonly pendingCount = signal(0);
  readonly urlCount = signal(0);
  readonly canAddUrl = signal(true);
  readonly usedCount = signal(0);
  readonly maxCount = signal(10);
  readonly canGenerate = signal(false);
  readonly canSave = signal(false);
  readonly canApprove = signal(false);
  readonly dirty = signal(false);
  readonly isApproved = signal(false);
  readonly gate = signal('sheet_not_approved');
  readonly isTrueCrime = signal(true);
  readonly savingFactSheet = signal(false);
  readonly approvingFactSheet = signal(false);
  readonly generating = signal(false);
  readonly error = signal<AppError | null>(null);
  readonly formError = signal<AppError | null>(null);
  readonly panelError = signal<AppError | null>(null);
  readonly conflict = signal(null);
  readonly rateLimitSeconds = signal<number | null>(null);
  readonly addCalls: { episodeId: string; url: string }[] = [];
  readonly removeCalls: { episodeId: string; sourceId: string }[] = [];
  readonly generateCalls: string[] = [];

  load() { return Promise.resolve({ ok: true as const, value: undefined }); }
  watch(): void { /* the page subscribes; nothing to record here */ }
  refresh() { return Promise.resolve({ ok: true as const, value: undefined }); }
  addUrl(episodeId: string, url: string) {
    this.addCalls.push({ episodeId, url });
    this.sources.update(list => [...list, source(9, { status: 'pending' })]);
    return Promise.resolve({ ok: true as const, value: undefined });
  }
  remove(episodeId: string, sourceId: string) {
    this.removeCalls.push({ episodeId, sourceId });
    this.sources.update(list => list.filter(item => item.id !== sourceId));
    return Promise.resolve({ ok: true as const, value: undefined });
  }
  generate(episodeId: string) {
    this.generateCalls.push(episodeId);
    return Promise.resolve({ ok: true as const, value: undefined });
  }
  clearFormError(): void { this.formError.set(null); }
  clearPanelError(): void { this.panelError.set(null); }
}

function confirmStub(answer: boolean) {
  return { confirm: vi.fn((data: { destructive?: boolean; titleKey?: string }) => ({ afterClosed: () => of(answer), data })) };
}

function flush(): Promise<void> {
  return new Promise(resolve => setTimeout(resolve, 0));
}

async function setup(store: FakeResearchStore, dialog = confirmStub(true)) {
  const params = convertToParamMap({ id: 'episode-a' });
  await TestBed.configureTestingModule({
    imports: [ResearchPageComponent, researchTranslations('en')],
    providers: [
      ...ZONELESS_TEST_PROVIDERS,
      provideRouter([]),
      { provide: ResearchStore, useValue: store },
      { provide: ConfirmDialog, useValue: dialog },
      { provide: ActivatedRoute, useValue: { paramMap: of(params), snapshot: { paramMap: params }, parent: null } },
    ],
    deferBlockBehavior: DeferBlockBehavior.Manual,
  }).compileComponents();
  const fixture = TestBed.createComponent(ResearchPageComponent);
  await fixture.whenStable();
  for (const block of await fixture.getDeferBlocks()) await block.render(DeferBlockState.Complete);
  await fixture.whenStable();
  return { fixture, root: fixture.nativeElement as HTMLElement, dialog };
}

afterEach(() => TestBed.resetTestingModule());

describe('ResearchPageComponent acceptance QA (10.5.5-T1)', () => {
  it('disables the add-source submit and states the reason at the source cap (110)', async () => {
    const store = new FakeResearchStore();
    store.usedCount.set(10);
    store.maxCount.set(10);
    const { root } = await setup(store);

    const submit = root.querySelector<HTMLButtonElement>('nts-source-add-form form button[type="submit"]');
    expect(submit?.disabled).toBe(true);
    expect(root.querySelector('nts-source-add-form p.hint')?.textContent?.trim() ?? '').toMatch(/\S/);
  });

  it('shows the pending row and clears the field after a link is added (111)', async () => {
    const store = new FakeResearchStore();
    const { fixture, root } = await setup(store);

    const input = root.querySelector<HTMLInputElement>('nts-source-add-form input[type="url"]');
    expect(input).not.toBeNull();
    input!.value = 'https://example.com/article';
    input!.dispatchEvent(new Event('input'));
    fixture.detectChanges();
    root.querySelector<HTMLButtonElement>('nts-source-add-form form button[type="submit"]')!.click();
    await flush();
    await fixture.whenStable();
    fixture.detectChanges();

    expect(store.addCalls).toEqual([{ episodeId: 'episode-a', url: 'https://example.com/article' }]);
    expect(root.querySelectorAll('nts-source-row')).toHaveLength(1);
    expect(root.querySelector('nts-source-row .source-status mat-spinner')).not.toBeNull();
    expect(input!.value).toBe('');
  });

  it('shows a domain-blocked add error inline and marks the URL field invalid (112)', async () => {
    const store = new FakeResearchStore();
    store.formError.set({ code: 'source_domain_not_allowed', status: 400, kind: 'validation', fieldErrors: {} });
    const { fixture, root } = await setup(store);
    fixture.detectChanges();
    await fixture.whenStable();

    const message = root.querySelector('nts-source-add-form .field-error p')?.textContent?.trim() ?? '';
    expect(message).not.toBe('');
    expect(message).not.toMatch(/^(errors|episodes)\./);
    expect(root.querySelector('nts-source-add-form input[type="url"]')?.getAttribute('aria-invalid')).toBe('true');
  });

  it('asks before removing a cited source and removes it only after confirm (115)', async () => {
    const store = new FakeResearchStore();
    store.sources.set([source(1, { isCited: true })]);
    const sourceId = store.sources()[0]!.id;
    const { fixture, root, dialog } = await setup(store, confirmStub(true));

    root.querySelector<HTMLButtonElement>('nts-source-row .source-actions button:last-of-type')!.click();
    await flush();
    await fixture.whenStable();

    expect(dialog.confirm).toHaveBeenCalledTimes(1);
    expect(dialog.confirm.mock.calls[0]?.[0]).toMatchObject({ destructive: true, titleKey: 'episodes.research.sources.remove' });
    expect(store.removeCalls).toEqual([{ episodeId: 'episode-a', sourceId }]);
  });

  it('keeps a cited source when its removal confirm is cancelled (115)', async () => {
    const store = new FakeResearchStore();
    store.sources.set([source(1, { isCited: true })]);
    const { fixture, root, dialog } = await setup(store, confirmStub(false));

    root.querySelector<HTMLButtonElement>('nts-source-row .source-actions button:last-of-type')!.click();
    await flush();
    await fixture.whenStable();

    expect(dialog.confirm).toHaveBeenCalledTimes(1);
    expect(store.removeCalls).toHaveLength(0);
    expect(store.sources()).toHaveLength(1);
  });

  it('removes an uncited source without asking for confirmation (115)', async () => {
    const store = new FakeResearchStore();
    store.sources.set([source(1, { isCited: false })]);
    const { fixture, root, dialog } = await setup(store, confirmStub(true));

    root.querySelector<HTMLButtonElement>('nts-source-row .source-actions button:last-of-type')!.click();
    await flush();
    await fixture.whenStable();

    expect(dialog.confirm).not.toHaveBeenCalled();
    expect(store.removeCalls).toHaveLength(1);
  });

  it('calls generate once when the panel Generate button is clicked (117)', async () => {
    const store = new FakeResearchStore();
    store.canGenerate.set(true);
    store.sources.set([source(1), source(2)]);
    const { fixture, root } = await setup(store);

    const button = root.querySelector<HTMLButtonElement>('button[aria-describedby="research-generate-reason"]');
    expect(button?.disabled).toBe(false);
    button!.click();
    await flush();
    await fixture.whenStable();

    expect(store.generateCalls).toEqual(['episode-a']);
  });
});
