import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { TranslocoService, TranslocoTestingModule } from '@jsverse/transloco';
import { afterEach, describe, expect, it } from 'vitest';
import { onEmit } from '@shared/ui/testing/on-emit';
import { ZONELESS_TEST_PROVIDERS } from '../../../../testing/providers';
import episodesEn from '../../../../../assets/i18n/episodes/en.json';
import episodesVi from '../../../../../assets/i18n/episodes/vi.json';
import errorsEn from '../../../../../assets/i18n/errors/en.json';
import errorsVi from '../../../../../assets/i18n/errors/vi.json';
import type { FailureCopy, RenderFailure, SkippedScene } from '../../domain/render.model';
import { RenderFailureComponent } from './render-failure.component';

const moderated: readonly SkippedScene[] = [{ segment: 'p:3', reason: 'moderated' }, { segment: 'not-an-address', reason: 'timeout' }];

async function setup(code = 'render_quality_failed', scenes: readonly SkippedScene[] = [], lang: 'vi' | 'en' = 'en', copy: FailureCopy = { titleKey: `errors.${code}`, bodyKeys: [], actionKeys: ['retry'] }) {
  await TestBed.configureTestingModule({
    imports: [RenderFailureComponent, TranslocoTestingModule.forRoot({
      langs: { en: { episodes: episodesEn, errors: errorsEn }, vi: { episodes: episodesVi, errors: errorsVi }, 'episodes/en': episodesEn, 'episodes/vi': episodesVi, 'errors/en': errorsEn, 'errors/vi': errorsVi },
      translocoConfig: { availableLangs: ['vi', 'en'], defaultLang: lang, fallbackLang: lang },
      preloadLangs: true,
    })],
    providers: [...ZONELESS_TEST_PROVIDERS, provideRouter([])],
  }).compileComponents();
  const fixture = TestBed.createComponent(RenderFailureComponent);
  const failure: RenderFailure & { detail: string } = { code, scenes, detailCode: 'private-detail-code', detail: 'private server detail must not render' };
  fixture.componentRef.setInput('failure', failure);
  fixture.componentRef.setInput('copy', copy);
  fixture.componentRef.setInput('actions', []);
  fixture.componentRef.setInput('episodeId', 'episode-1');
  await fixture.whenStable();
  return { fixture, component: fixture.componentInstance, root: fixture.nativeElement as HTMLElement };
}

afterEach(() => TestBed.resetTestingModule());

describe('RenderFailureComponent', () => {
  it.each(['vi', 'en'] as const)('renders a headed alert and never leaks details in %s', async (lang) => {
    const { root } = await setup('render_quality_failed', [], lang);
    expect(root.querySelector('[data-testid="render-failure"]')?.getAttribute('role')).toBe('alert');
    expect(root.querySelector('h2')?.textContent).toBe(TestBed.inject(TranslocoService).translate('errors.render_quality_failed'));
    expect(root.textContent).not.toContain('private server detail');
    expect(root.textContent).not.toContain('private-detail-code');
    expect(root.querySelector('[data-testid="render-failure-scenes"]')).toBeNull();
    expect(root.textContent).toContain(TestBed.inject(TranslocoService).translate('episodes.render.failure.generic'));
  });

  it.each(['image_moderated', 'image_generation_failed', 'tts_failed', 'render_quality_failed', 'render_node_error', 'render_assembly_failed', 'render_lost', 'render_timeout', 'render_output_invalid'])('renders the supplied catalogued title for %s', async (code) => {
    const { root } = await setup(code);
    expect(root.querySelector('h2')?.textContent).toBe(TestBed.inject(TranslocoService).translate(`errors.${code}`));
  });

  it.each(['images_incomplete', 'provider_funds_exhausted', 'upstream_error'])('uses the approved generic title and body for %s', async (code) => {
    const { root } = await setup(code, [], 'en', { titleKey: 'render.failure.title', bodyKeys: [`render.failure.code.${code}`], actionKeys: [] });
    expect(root.querySelector('h2')?.textContent).toBe(TestBed.inject(TranslocoService).translate('episodes.render.failure.title'));
    expect(root.textContent).toContain(TestBed.inject(TranslocoService).translate(`episodes.render.failure.code.${code}`));
  });

  it('enables retry only with the offered action and matching server nextAction, then emits intent', async () => {
    const { fixture, component, root } = await setup();
    const retry = root.querySelector<HTMLButtonElement>('button')!;
    expect(retry.disabled).toBe(true);
    const intents: string[] = [];
    onEmit(component.retry, () => intents.push('retry'));
    retry.click();
    expect(intents).toEqual([]);
    fixture.componentRef.setInput('actions', ['reopen_story']);
    await fixture.whenStable();
    expect(retry.disabled).toBe(true);
    fixture.componentRef.setInput('actions', ['retry_render']);
    await fixture.whenStable();
    expect(retry.disabled).toBe(false);
    retry.click();
    expect(intents).toEqual(['retry']);
    fixture.componentRef.setInput('copy', { titleKey: 'render.failure.title', bodyKeys: [], actionKeys: [] });
    await fixture.whenStable();
    expect(root.querySelector('button')).toBeNull();
  });

  it('lists scenes with link/plain-text and keeps guidance visible when editing is unavailable', async () => {
    const { fixture, component, root } = await setup('image_moderated', moderated, 'en', { titleKey: 'errors.image_moderated', bodyKeys: [], actionKeys: ['editScenes'] });
    const list = root.querySelector('[data-testid="render-failure-scenes"]')!;
    expect(list.querySelectorAll('li')).toHaveLength(2);
    expect(list.querySelector('a')?.getAttribute('href')).toBe('/episodes/episode-1?segment=p:3');
    expect(list.querySelectorAll('a')).toHaveLength(1);
    expect(list.textContent).not.toContain('not-an-address');
    expect(list.textContent).toContain(TestBed.inject(TranslocoService).translate('episodes.render.images.sceneUnnamed', { index: 2 }));
    expect(list.textContent).toContain(TestBed.inject(TranslocoService).translate('episodes.render.images.moderatedScene'));
    expect(root.textContent).toContain(TestBed.inject(TranslocoService).translate('episodes.render.failure.editScenesHint'));
    expect(root.querySelector('button')).toBeNull();
    fixture.componentRef.setInput('actions', ['reopen_story']);
    await fixture.whenStable();
    expect(root.querySelector('button')).toBeNull();
    fixture.componentRef.setInput('canEditScenes', true);
    await fixture.whenStable();
    const button = root.querySelector<HTMLButtonElement>('button')!;
    expect(button.textContent?.trim()).toBe(TestBed.inject(TranslocoService).translate('episodes.render.failure.editScenes'));
    const hint = root.querySelector('.edit-hint')!;
    expect(button.getAttribute('aria-describedby')).toBe(hint.id);
    let edited = 0;
    onEmit(component.editScenes, () => edited++);
    button.click();
    expect(edited).toBe(1);
    fixture.componentRef.setInput('actions', ['retry_render']);
    await fixture.whenStable();
    expect(root.querySelector('button')).toBeNull();
  });

  it.each(['image_moderated', 'image_generation_failed'])('offers no edit action without a moderated scene for %s', async (code) => {
    const { fixture, root } = await setup(code, [{ segment: 'p:3', reason: 'timeout' }], 'en', { titleKey: `errors.${code}`, bodyKeys: [], actionKeys: [] });
    fixture.componentRef.setInput('actions', ['reopen_story', 'retry_render']);
    fixture.componentRef.setInput('canEditScenes', true);
    await fixture.whenStable();
    expect(root.querySelector('button')).toBeNull();
  });

  it('renders an unknown code as generic copy with no invented action', async () => {
    const { fixture, root } = await setup('private-new-code', [], 'en', { titleKey: 'render.failure.title', bodyKeys: [], actionKeys: [] });
    fixture.componentRef.setInput('actions', ['reopen_story', 'retry_render']);
    fixture.componentRef.setInput('canEditScenes', true);
    await fixture.whenStable();
    expect(root.querySelector('h2')?.textContent).toBe(TestBed.inject(TranslocoService).translate('episodes.render.failure.title'));
    expect(root.textContent).not.toContain('private-new-code');
    expect(root.querySelector('button')).toBeNull();
  });
});
