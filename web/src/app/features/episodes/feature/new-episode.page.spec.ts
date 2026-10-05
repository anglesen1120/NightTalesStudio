import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { FormControl, FormGroup, Validators } from '@angular/forms';
import { ActivatedRoute, convertToParamMap, provideRouter } from '@angular/router';
import { TranslocoTestingModule, provideTranslocoScope } from '@jsverse/transloco';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { ZONELESS_TEST_PROVIDERS } from '../../../testing/providers';
import coreVi from '../../../../assets/i18n/core/vi.json';
import episodesVi from '../../../../assets/i18n/episodes/vi.json';
import errorsVi from '../../../../assets/i18n/errors/vi.json';
import storyVi from '../../../../assets/i18n/story/vi.json';
import { NewEpisodeStore } from '../application/new-episode.store';
import type { LengthTier } from '../domain/episode.model';
import { NewEpisodePage } from './new-episode.page';

const SERIES_ID = '11111111-1111-4111-8111-111111111111';
const translations = TranslocoTestingModule.forRoot({
  langs: { vi: coreVi, 'episodes/vi': episodesVi, 'errors/vi': errorsVi, 'story/vi': storyVi },
  translocoConfig: { availableLangs: ['vi'], defaultLang: 'vi', fallbackLang: 'vi', reRenderOnLangChange: true },
  preloadLangs: true,
});

function storeStub() {
  return {
    form: new FormGroup({
      title: new FormControl<string | null>(null, Validators.maxLength(120)),
      brief: new FormControl('Một chuyến xe giữa đêm', { nonNullable: true, validators: [(control) => Validators.required(control), Validators.maxLength(1000)] }),
      lengthTier: new FormControl<LengthTier>('standard', { nonNullable: true }),
    }),
    phase: signal<'form' | 'submitting' | 'generating' | 'failed' | 'paused' | 'handoff'>('form'),
    briefLength: signal(23), isTrueCrime: signal(false), submitting: signal(false), canSubmit: signal(true),
    retryAfterSec: signal<number | null>(null), unmappedFieldKeys: signal<readonly string[]>([]),
    errorKey: signal<string | null>(null), traceId: signal<string | null>(null), createLost: signal(false),
    gateBlocked: signal(false), factSheetRequired: signal(false), episodeId: signal<string | null>('episode-1'),
    stageKey: signal<string | null>(null), failureKey: signal<string | null>(null),
    paused: signal<string | null>(null), canRetry: signal(false),
    init: vi.fn<(id: string) => void>(), create: vi.fn<() => Promise<void>>().mockResolvedValue(undefined),
    retry: vi.fn<() => Promise<void>>().mockResolvedValue(undefined), countdownElapsed: vi.fn<() => void>(),
  };
}

async function setup() {
  const store = storeStub();
  TestBed.overrideComponent(NewEpisodePage, {
    set: { providers: [{ provide: NewEpisodeStore, useValue: store }, provideTranslocoScope('episodes', 'story', 'errors')] },
  });
  await TestBed.configureTestingModule({
    imports: [NewEpisodePage, translations],
    providers: [
      ...ZONELESS_TEST_PROVIDERS, provideRouter([]),
      { provide: ActivatedRoute, useValue: { snapshot: { paramMap: convertToParamMap({ id: SERIES_ID }) } } },
    ],
  }).compileComponents();
  const fixture = TestBed.createComponent(NewEpisodePage);
  fixture.detectChanges();
  await fixture.whenStable();
  return { store, fixture, root: fixture.nativeElement as HTMLElement };
}

afterEach(() => { TestBed.resetTestingModule(); vi.useRealTimers(); });

describe('NewEpisodePage', () => {
  it('initializes from the route snapshot id and forwards a valid submit to the store', async () => {
    const { store, root } = await setup();
    expect(store.init).toHaveBeenCalledExactlyOnceWith(SERIES_ID);
    root.querySelector('form')?.dispatchEvent(new Event('submit', { bubbles: true, cancelable: true }));
    expect(store.create).toHaveBeenCalledTimes(1);
  });

  it('focuses the first client-invalid control after an Enter submit, not an async alert', async () => {
    const { store, fixture, root } = await setup();
    store.form.controls.title.setValue('x'.repeat(121));
    store.form.controls.brief.setValue('');
    store.canSubmit.set(false);
    fixture.detectChanges();
    await fixture.whenStable();
    root.querySelector('form')?.dispatchEvent(new Event('submit', { bubbles: true, cancelable: true }));
    expect(document.activeElement).toBe(root.querySelector('[data-control="title"]'));
    expect(store.create).not.toHaveBeenCalled();
    const focused = document.activeElement;
    store.errorKey.set('errors.validation_failed');
    fixture.detectChanges();
    await fixture.whenStable();
    expect(document.activeElement).toBe(focused);
  });

  it('renders successive authoritative stage keys without percentages or focus moves', async () => {
    const { store, fixture, root } = await setup();
    store.phase.set('generating');
    store.stageKey.set('story.generation.stage.writing');
    fixture.detectChanges();
    await fixture.whenStable();
    const stage = root.querySelector('[aria-live="polite"]');
    expect(stage?.textContent).toContain(storyVi.generation.stage.writing);
    const focused = document.activeElement;
    store.stageKey.set('story.generation.stage.reviewing');
    await fixture.whenStable();
    expect(stage?.textContent).toContain(storyVi.generation.stage.reviewing);
    expect(document.activeElement).toBe(focused);
    expect(root.querySelector('nts-story-brief-form')).toBeNull();
    expect(root.textContent).not.toMatch(/\d+%/);
  });

  it('keeps the created episode link next to a failure and delegates retry to the store', async () => {
    const { store, fixture, root } = await setup();
    store.phase.set('failed');
    store.failureKey.set('story.generation.error.story_invalid_output');
    store.canRetry.set(true);
    fixture.detectChanges();
    await fixture.whenStable();
    expect(root.querySelector('[role="alert"]')?.textContent).toContain(storyVi.generation.error.story_invalid_output);
    expect(root.querySelector('a[href="/episodes/episode-1"]')).not.toBeNull();
    root.querySelector<HTMLButtonElement>('.generation-progress__retry')?.click();
    expect(store.retry).toHaveBeenCalledTimes(1);
  });

  it('keeps true-crime form copy and the defensive fact-sheet-required research link gated', async () => {
    const { store, fixture, root } = await setup();
    store.isTrueCrime.set(true);
    fixture.detectChanges();
    await fixture.whenStable();
    expect(root.querySelector('button[type="submit"]')?.textContent).toContain(episodesVi.new.trueCrimeNext);
    store.phase.set('failed');
    store.factSheetRequired.set(true);
    store.errorKey.set('errors.fact_sheet_required');
    store.canRetry.set(true);
    fixture.detectChanges();
    await fixture.whenStable();
    expect(root.querySelector('a[href="/episodes/episode-1/research"]')).not.toBeNull();
    expect(root.querySelector('.generation-progress__retry')).toBeNull();
    expect(root.textContent).toContain(episodesVi.research.gate.writeStoryBlocked);
  });

  it('shows the 429 seconds and a disabled retry, then delegates countdown completion', async () => {
    const { store, fixture, root } = await setup();
    vi.useFakeTimers();
    store.phase.set('failed');
    store.retryAfterSec.set(2);
    store.canRetry.set(false);
    fixture.detectChanges();
    expect(root.querySelector('nts-retry-countdown')?.textContent).toContain('2 giây');
    expect(root.querySelector<HTMLButtonElement>('.new-episode-page__countdown button')?.disabled).toBe(true);
    expect(root.querySelector('.generation-progress__retry')).toBeNull();
    vi.advanceTimersByTime(2000);
    fixture.detectChanges();
    expect(store.countdownElapsed).toHaveBeenCalledTimes(1);
  });
});
