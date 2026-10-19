import { TestBed } from '@angular/core/testing';
import { TranslocoTestingModule } from '@jsverse/transloco';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { onEmit } from '@shared/ui/testing/on-emit';
import { ZONELESS_TEST_PROVIDERS } from '../../../../testing/providers';
import episodesEn from '../../../../../assets/i18n/episodes/en.json';
import coreEn from '../../../../../assets/i18n/core/en.json';
import { VideoPlayerComponent } from './video-player.component';

async function setup() {
  const loadSpy = vi.spyOn(HTMLMediaElement.prototype, 'load').mockImplementation(() => undefined);
  await TestBed.configureTestingModule({
    imports: [VideoPlayerComponent, TranslocoTestingModule.forRoot({ langs: { en: { ...coreEn, episodes: episodesEn } }, translocoConfig: { availableLangs: ['en'], defaultLang: 'en' }, preloadLangs: true })],
    providers: ZONELESS_TEST_PROVIDERS,
  }).compileComponents();
  const fixture = TestBed.createComponent(VideoPlayerComponent);
  fixture.componentRef.setInput('label', episodesEn.render.review.heading);
  fixture.componentRef.setInput('url', 'https://media.example/video.mp4?signature=private-first');
  await fixture.whenStable();
  return { fixture, root: fixture.nativeElement as HTMLElement, loadSpy };
}

afterEach(() => { TestBed.resetTestingModule(); vi.restoreAllMocks(); });

describe('VideoPlayerComponent', () => {
  it('renders its media element with the component, with no defer of its own (C9)', async () => {
    const { fixture, root } = await setup();
    expect(await fixture.getDeferBlocks()).toHaveLength(0);
    expect(root.querySelector('video')).not.toBeNull();
    expect(root.querySelector('nts-loading-state')).toBeNull();
  });

  it('uses labelled native metadata-only controls, never autoplay, poster or false captions', async () => {
    const { root } = await setup();
    const video = root.querySelector('video')!;
    expect(video.controls).toBe(true);
    expect(video.preload).toBe('metadata');
    expect(video.getAttribute('aria-label')).toBe(episodesEn.render.review.heading);
    expect(video.hasAttribute('autoplay')).toBe(false);
    expect(video.hasAttribute('poster')).toBe(false);
    expect(video.hasAttribute('muted')).toBe(false);
    expect(video.querySelector('track')).toBeNull();
    expect(root.textContent).toContain(episodesEn.render.review.noCaptions);
    expect(root.textContent).not.toContain('signature=');
  });

  it('swaps src and loads the same element, preserving seek intent and a silent count-only hook', async () => {
    const { fixture, root, loadSpy } = await setup();
    const video = root.querySelector('video')!;
    video.currentTime = 42;
    fixture.componentRef.setInput('url', 'https://media.example/video.mp4?signature=private-next');
    await fixture.whenStable();
    expect(root.querySelector('video')).toBe(video);
    expect(video.getAttribute('src')).toContain('private-next');
    expect(loadSpy).toHaveBeenCalledTimes(2);
    video.dispatchEvent(new Event('loadedmetadata'));
    expect(video.currentTime).toBe(42);
    const hook = root.querySelector<HTMLElement>('[data-testid="video-url-refreshed"]')!;
    expect(hook.textContent).toBe('1');
    expect(hook.hidden).toBe(true);
    expect(hook.getAttribute('aria-hidden')).toBe('true');
    expect(hook.hasAttribute('aria-live')).toBe(false);
    for (const element of root.querySelectorAll('*')) {
      for (const attribute of element.attributes) {
        if (attribute.value.includes('signature=')) expect([element.tagName, attribute.name]).toEqual(['VIDEO', 'src']);
      }
    }
  });

  it('emits once for each native error event', async () => {
    const { fixture, root } = await setup();
    let count = 0;
    onEmit(fixture.componentInstance.mediaError, () => count++);
    root.querySelector('video')!.dispatchEvent(new Event('error'));
    expect(count).toBe(1);
    root.querySelector('video')!.dispatchEvent(new Event('error'));
    expect(count).toBe(2);
  });
});
