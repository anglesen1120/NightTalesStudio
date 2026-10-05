import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { TranslocoTestingModule } from '@jsverse/transloco';
import { afterEach, describe, expect, it } from 'vitest';
import { onEmit } from '@shared/ui/testing/on-emit';
import { ZONELESS_TEST_PROVIDERS } from '../../../testing/providers';
import coreVi from '../../../../assets/i18n/core/vi.json';
import episodesVi from '../../../../assets/i18n/episodes/vi.json';
import errorsVi from '../../../../assets/i18n/errors/vi.json';
import storyVi from '../../../../assets/i18n/story/vi.json';
import { GenerationProgressComponent } from './generation-progress.component';

const translations = TranslocoTestingModule.forRoot({
  langs: { vi: coreVi, 'episodes/vi': episodesVi, 'errors/vi': errorsVi, 'story/vi': storyVi },
  translocoConfig: { availableLangs: ['vi'], defaultLang: 'vi', fallbackLang: 'vi', reRenderOnLangChange: true },
  preloadLangs: true,
});

async function setup(stageKey: string | null = null) {
  await TestBed.configureTestingModule({
    imports: [GenerationProgressComponent, translations],
    providers: [...ZONELESS_TEST_PROVIDERS, provideRouter([])],
  }).compileComponents();
  const fixture = TestBed.createComponent(GenerationProgressComponent);
  fixture.componentRef.setInput('stageKey', stageKey);
  fixture.detectChanges();
  await fixture.whenStable();
  return { fixture, root: fixture.nativeElement as HTMLElement, component: fixture.componentInstance };
}

afterEach(() => TestBed.resetTestingModule());

describe('GenerationProgressComponent', () => {
  it.each([
    ['episodes.generation.stage.queued', episodesVi.generation.stage.queued],
    ...Object.entries(storyVi.generation.stage).map(([stage, copy]) => [`story.generation.stage.${stage}`, copy]),
    ['episodes.generation.stage.done', episodesVi.generation.stage.done],
  ])('renders %s in the polite stage line', async (key, copy) => {
    const { root } = await setup(key);
    expect(root.querySelector('.generation-progress__stage[aria-live="polite"]')?.textContent?.trim()).toBe(copy);
    expect(root.querySelector('button')).toBeNull();
    expect(root.textContent).not.toMatch(/\d+%|elapsed|giây/);
  });

  it.each([null, 'provider-new-stage', 'story.generation.stage.unknown'])('renders no unknown stage text (%s)', async (key) => {
    const { root } = await setup(key);
    expect(root.querySelector('.generation-progress__stage')?.textContent?.trim()).toBe('');
    if (key !== null) expect(root.textContent).not.toContain(key);
  });

  it('renders terminal failure as an alert and emits retry without moving focus', async () => {
    const { fixture, root, component } = await setup('story.generation.stage.checking');
    const focus = document.activeElement;
    fixture.componentRef.setInput('failureKey', 'story.generation.error.story_invalid_output');
    fixture.componentRef.setInput('canRetry', true);
    fixture.componentRef.setInput('episodeId', 'episode-1');
    fixture.detectChanges();
    await fixture.whenStable();
    expect(root.querySelector('[role="alert"]')?.textContent).toContain(storyVi.generation.error.story_invalid_output);
    expect(document.activeElement).toBe(focus);
    expect(root.querySelector('a')?.getAttribute('href')).toBe('/episodes/episode-1');
    let retried = 0;
    onEmit(component.retry, () => retried++);
    root.querySelector<HTMLButtonElement>('button')?.click();
    expect(retried).toBe(1);
  });

  it('renders pause as a status notice with neither an error nor retry, even with stale failure inputs', async () => {
    const { fixture, root } = await setup();
    fixture.componentRef.setInput('paused', 'provider_funds_exhausted');
    fixture.componentRef.setInput('failureKey', 'story.generation.error.story_invalid_output');
    fixture.componentRef.setInput('canRetry', true);
    fixture.detectChanges();
    await fixture.whenStable();
    expect(root.querySelector('[role="status"]')?.textContent).toContain(storyVi.generation.paused.provider_funds_exhausted);
    expect(root.querySelector('[role="alert"]')).toBeNull();
    expect(root.querySelector('button')).toBeNull();
  });

  it('uses a safe paused fallback instead of rendering an unknown backend reason', async () => {
    const { fixture, root } = await setup();
    fixture.componentRef.setInput('paused', 'NEW_RAW_BACKEND_REASON');
    fixture.detectChanges();
    await fixture.whenStable();
    expect(root.querySelector('[role="status"]')?.textContent).toContain(episodesVi.generation.paused);
    expect(root.textContent).not.toContain('NEW_RAW_BACKEND_REASON');
  });

  it('disables the retry during a retry request and ignores repeated activation', async () => {
    const { fixture, root, component } = await setup();
    fixture.componentRef.setInput('failureKey', 'errors.network_error');
    fixture.componentRef.setInput('canRetry', true);
    fixture.componentRef.setInput('retrying', true);
    fixture.detectChanges();
    await fixture.whenStable();
    const button = root.querySelector<HTMLButtonElement>('button');
    expect(button?.disabled).toBe(true);
    expect(button?.getAttribute('aria-busy')).toBe('true');
    expect(root.querySelector('[role="status"]')?.textContent).toContain(episodesVi.generation.retrying);
    let retried = 0;
    onEmit(component.retry, () => retried++);
    button?.click();
    expect(retried).toBe(0);
  });
});
