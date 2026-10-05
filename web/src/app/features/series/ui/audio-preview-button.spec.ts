import { ChangeDetectionStrategy, Component, signal } from '@angular/core';
import { TestBed, type ComponentFixture } from '@angular/core/testing';
import { TranslocoTestingModule } from '@jsverse/transloco';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { ZONELESS_TEST_PROVIDERS } from '../../../testing/providers';
import { AudioPreviewButton } from './audio-preview-button';

// Inline scope translations (the real files are delivered alongside; specs never read them).
const seriesVi = {
  voice: {
    preview: 'Nghe thử',
    stop: 'Dừng nghe',
    previewUnavailable: 'Chưa có bản nghe thử cho giọng này',
    previewFailed: 'Không phát được bản nghe thử',
  },
};
const seriesEn = {
  voice: {
    preview: 'Play preview',
    stop: 'Stop preview',
    previewUnavailable: 'No preview is available for this voice',
    previewFailed: 'The preview could not be played',
  },
};

const translations = TranslocoTestingModule.forRoot({
  langs: { vi: {}, en: {}, 'series/vi': seriesVi, 'series/en': seriesEn },
  translocoConfig: {
    availableLangs: ['vi', 'en'],
    defaultLang: 'vi',
    fallbackLang: 'vi',
    reRenderOnLangChange: true,
  },
  preloadLangs: true,
});

@Component({
  imports: [AudioPreviewButton],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `<nts-audio-preview-button
    [previewUrl]="url()"
    displayName="Thiện Minh"
    [playing]="playing()"
    [disabled]="disabled()"
    (started)="starts = starts + 1"
    (stopped)="stops = stops + 1"
  />`,
})
class PreviewButtonHost {
  readonly url = signal<string | null>('/media/minh.mp3');
  readonly playing = signal(false);
  readonly disabled = signal(false);
  starts = 0;
  stops = 0;
}

let playCalls: HTMLMediaElement[];

beforeEach(() => {
  playCalls = [];
  // jsdom media stubs: pause/load are inert, play is stubbed so the activation path is observable.
  vi.spyOn(HTMLMediaElement.prototype, 'pause').mockImplementation(() => undefined);
  vi.spyOn(HTMLMediaElement.prototype, 'load').mockImplementation(() => undefined);
  vi.spyOn(HTMLMediaElement.prototype, 'play').mockImplementation(function (this: HTMLMediaElement) {
    playCalls.push(this);
    return Promise.resolve();
  });
});

afterEach(() => {
  TestBed.resetTestingModule();
  vi.restoreAllMocks();
});

async function setup(): Promise<{ fixture: ComponentFixture<PreviewButtonHost>; host: PreviewButtonHost; root: HTMLElement }> {
  await TestBed.configureTestingModule({
    imports: [PreviewButtonHost, translations],
    providers: ZONELESS_TEST_PROVIDERS,
  }).compileComponents();
  const fixture = TestBed.createComponent(PreviewButtonHost);
  fixture.detectChanges();
  await fixture.whenStable();
  return { fixture, host: fixture.componentInstance, root: fixture.nativeElement as HTMLElement };
}

function button(root: HTMLElement): HTMLButtonElement {
  const el = root.querySelector<HTMLButtonElement>('button');
  if (el === null) throw new Error('missing preview button');
  return el;
}

describe('AudioPreviewButton', () => {
  it('NullPreview_RendersDisabledButton_AndNoAudioElement', async () => {
    const { fixture, host, root } = await setup();
    host.url.set(null);
    fixture.detectChanges();
    await fixture.whenStable();

    const el = button(root);
    expect(el.disabled).toBe(true);
    expect(el.getAttribute('title')).toBe('Chưa có bản nghe thử cho giọng này');
    expect(el.getAttribute('aria-label')).toBe('Chưa có bản nghe thử cho giọng này — Thiện Minh');
    expect(root.querySelector('audio')).toBeNull();
    expect(playCalls.length).toBe(0);
  });

  it('Preview_RendersAudioInsideDefer_AndStartsOnActivation', async () => {
    const { fixture, host, root } = await setup();
    // Before any interaction there is no <audio> at all (zero media request — D13/F12).
    expect(root.querySelector('audio')).toBeNull();

    button(root).click();
    fixture.detectChanges();
    await fixture.whenStable();

    const audio = root.querySelector('audio');
    expect(audio).not.toBeNull();
    expect(audio?.getAttribute('preload')).toBe('none');
    expect(audio?.getAttribute('src')).toContain('/media/minh.mp3');
    expect(host.starts).toBe(1);
    // The activation emitted `started` but playback begins only when the parent marks it playing.
    expect(playCalls.length).toBe(0);

    host.playing.set(true);
    fixture.detectChanges();
    await fixture.whenStable();
    expect(playCalls.length).toBe(1);
    expect(playCalls[0]).toBe(audio);
  });

  it('Started_EmitsOnce', async () => {
    const { fixture, host, root } = await setup();
    button(root).click();
    fixture.detectChanges();
    await fixture.whenStable();
    expect(host.starts).toBe(1);

    // The parent granted playback; a second press is a stop, not another start.
    host.playing.set(true);
    fixture.detectChanges();
    await fixture.whenStable();
    button(root).click();
    fixture.detectChanges();
    await fixture.whenStable();
    expect(host.starts).toBe(1);
    expect(host.stops).toBe(1);
  });

  it('AriaPressed_TracksPlayback', async () => {
    const { fixture, host, root } = await setup();
    const el = button(root);
    expect(el.getAttribute('aria-pressed')).toBe('false');

    host.playing.set(true);
    fixture.detectChanges();
    await fixture.whenStable();
    expect(el.getAttribute('aria-pressed')).toBe('true');

    host.playing.set(false);
    fixture.detectChanges();
    await fixture.whenStable();
    expect(el.getAttribute('aria-pressed')).toBe('false');
  });

  it('Error_ShowsPreviewFailed_NoRetryLoop', async () => {
    const { fixture, host, root } = await setup();
    button(root).click();
    fixture.detectChanges();
    await fixture.whenStable();
    host.playing.set(true);
    fixture.detectChanges();
    await fixture.whenStable();
    expect(playCalls.length).toBe(1);

    const audio = root.querySelector('audio');
    audio?.dispatchEvent(new Event('error'));
    fixture.detectChanges();
    await fixture.whenStable();

    expect(root.textContent).toContain('Không phát được bản nghe thử');
    expect(root.querySelector('[aria-live="polite"]')).not.toBeNull();
    expect(host.stops).toBe(1);
    // No automatic retry: several change-detection cycles never call play() again.
    for (let i = 0; i < 3; i++) {
      fixture.detectChanges();
      await fixture.whenStable();
    }
    expect(playCalls.length).toBe(1);
  });

  it('PlayRejected_NotAllowedError_ShowsPreviewFailed_StopsOnce_AndDoesNotRetry', async () => {
    // PS-2: a rejected play() (autoplay policy) is a real failure, not a swallowed one.
    const { fixture, host, root } = await setup();
    vi.spyOn(HTMLMediaElement.prototype, 'play').mockImplementation(function (this: HTMLMediaElement) {
      playCalls.push(this);
      return Promise.reject(new DOMException('autoplay blocked', 'NotAllowedError'));
    });

    button(root).click();
    fixture.detectChanges();
    await fixture.whenStable();
    host.playing.set(true);
    fixture.detectChanges();
    await fixture.whenStable();
    expect(playCalls.length).toBe(1);

    // The rejection handler runs in a microtask; `stopped` flips before any re-render.
    await vi.waitFor(() => expect(host.stops).toBe(1));
    fixture.detectChanges();
    await fixture.whenStable();

    expect(root.textContent).toContain('Không phát được bản nghe thử');
    expect(root.querySelector('[aria-live="polite"]')).not.toBeNull();
    expect(button(root).getAttribute('aria-pressed')).toBe('false');
    expect(host.stops).toBe(1);

    // No retry loop: later change-detection cycles never call play() again.
    for (let i = 0; i < 3; i++) {
      fixture.detectChanges();
      await fixture.whenStable();
    }
    expect(playCalls.length).toBe(1);
  });

  it('PlayRejected_AbortError_IsIgnored', async () => {
    // PS-2: `AbortError` is our own pause() interrupting play(); it must not surface as a failure.
    const { fixture, host, root } = await setup();
    vi.spyOn(HTMLMediaElement.prototype, 'play').mockImplementation(function (this: HTMLMediaElement) {
      playCalls.push(this);
      return Promise.reject(new DOMException('interrupted by pause()', 'AbortError'));
    });

    button(root).click();
    fixture.detectChanges();
    await fixture.whenStable();
    host.playing.set(true);
    fixture.detectChanges();
    await fixture.whenStable();
    expect(playCalls.length).toBe(1);

    await Promise.resolve();
    await Promise.resolve();
    fixture.detectChanges();
    await fixture.whenStable();

    expect(host.stops).toBe(0);
    expect(root.textContent).not.toContain('Không phát được bản nghe thử');
    expect(button(root).getAttribute('aria-pressed')).toBe('true');
  });

  it('Destroy_StopsAndReleasesAudio', async () => {
    const { fixture, host, root } = await setup();
    button(root).click();
    fixture.detectChanges();
    await fixture.whenStable();
    host.playing.set(true);
    fixture.detectChanges();
    await fixture.whenStable();
    const audio = root.querySelector('audio');
    expect(audio).not.toBeNull();
    const pauseSpy = vi.spyOn(audio as HTMLAudioElement, 'pause');
    const loadSpy = vi.spyOn(audio as HTMLAudioElement, 'load');

    fixture.destroy();

    expect(pauseSpy).toHaveBeenCalled();
    expect(loadSpy).toHaveBeenCalled();
    expect(audio?.getAttribute('src')).toBeNull();
  });
});
