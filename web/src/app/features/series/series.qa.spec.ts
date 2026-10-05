import { LiveAnnouncer } from '@angular/cdk/a11y';
import { ChangeDetectionStrategy, Component, signal } from '@angular/core';
import { TestBed, type ComponentFixture } from '@angular/core/testing';
import { TranslocoTestingModule } from '@jsverse/transloco';
import type { AppError } from '@core/errors';
import { SessionApi } from '@core/session';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { FakeSessionApi } from '../../core/session/testing/fake-session.api';
import { ZONELESS_TEST_PROVIDERS } from '../../testing/providers';
import { CatalogRepository } from './application/catalog.repository';
import { VoiceStepStore } from './application/voice-step.store';
import { safePreviewUrl } from './domain/preview-url';
import { toVoicePickerViewModel } from './domain/voice-picker.view-model';
import type { Voice } from './domain/voice.model';
import { FakeCatalogRepository } from './testing/fake-catalog.repository';
import { VoicePicker } from './ui/voice-picker';

const translations = TranslocoTestingModule.forRoot({
  langs: {
    vi: { common: { back: 'Quay lại' } },
    'series/vi': {
      field: { voice: 'Giọng đọc' },
      voice: {
        preview: 'Nghe thử',
        stop: 'Dừng nghe',
        previewUnavailable: 'Chưa có bản nghe thử',
        previewFailed: 'Lỗi nghe thử',
        rateFixed: 'Tốc độ đọc được cố định',
        empty: 'Chưa có giọng đọc',
        clearedByLanguage: 'Đã xoá giọng đọc',
        gender: { male: 'Nam', female: 'Nữ', neutral: 'Trung tính' },
      },
    },
    'voices/vi': { tag: { warm: 'Ấm', calm: 'Điềm tĩnh' } },
    'errors/vi': { action: { retry: 'Thử lại' } },
  },
  translocoConfig: { availableLangs: ['vi'], defaultLang: 'vi', fallbackLang: 'vi', reRenderOnLangChange: true },
  preloadLangs: true,
});

function voice(overrides: Partial<Voice> = {}): Voice {
  return { id: 'v1', displayName: 'Lan', language: 'vi', gender: 'female', tags: ['warm'], previewUrl: null, ...overrides };
}

describe('safePreviewUrl (QA edge cases)', () => {
  it.each([
    ['upper-case javascript scheme', 'JAVASCRIPT:alert(1)'],
    ['zero-width prefix', '\u200bhttps://example.com/a.mp3'],
    ['no-break space prefix', '\u00a0https://example.com/a.mp3'],
    ['scheme without host', 'https://'],
    ['bare backslash', '\\evil.example/a.mp3'],
    ['file scheme', 'file:///etc/passwd'],
    ['double slash with backslash', '/\\/evil.example/a.mp3'],
    ['vbscript scheme', 'vbscript:msgbox(1)'],
  ])('rejects %s', (_name, url) => expect(safePreviewUrl(url)).toBeNull());

  it('keeps a root-relative path with a query verbatim', () => {
    expect(safePreviewUrl('/media/a.mp3?v=2')).toBe('/media/a.mp3?v=2');
  });
});

describe('toVoicePickerViewModel (QA)', () => {
  it('drops unknown tags, keeps order, and nulls an unsafe preview url', () => {
    const vm = toVoicePickerViewModel([
      voice({ tags: ['calm', 'bogus' as never, 'warm'], previewUrl: 'javascript:alert(1)' }),
    ]);
    expect(vm.rows[0]!.tagKeys).toEqual(['voices.tag.calm', 'voices.tag.warm']);
    expect(vm.rows[0]!.preview.url).toBeNull();
  });

  it('renders an unknown gender raw with no translation key', () => {
    const row = toVoicePickerViewModel([voice({ gender: 'robot' })]).rows[0]!;
    expect(row.genderKey).toBeNull();
    expect(row.genderRaw).toBe('robot');
  });
});

@Component({
  imports: [VoicePicker],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `<nts-voice-picker
    [voices]="voices()"
    [selectedId]="selectedId()"
    [loading]="loading()"
    (selected)="selectedId.set($event); selects.push($event)"
    (backToLanguage)="backClicks = backClicks + 1"
  />`,
})
class PickerHost {
  readonly voices = signal<readonly Voice[]>([]);
  readonly selectedId = signal<string | null>(null);
  readonly loading = signal(false);
  readonly selects: string[] = [];
  backClicks = 0;
}

async function setupPicker(initial: readonly Voice[]): Promise<{ fixture: ComponentFixture<PickerHost>; host: PickerHost; root: HTMLElement }> {
  await TestBed.configureTestingModule({
    imports: [PickerHost, translations],
    providers: ZONELESS_TEST_PROVIDERS,
  }).compileComponents();
  const fixture = TestBed.createComponent(PickerHost);
  fixture.componentInstance.voices.set(initial);
  fixture.detectChanges();
  await fixture.whenStable();
  return { fixture, host: fixture.componentInstance, root: fixture.nativeElement as HTMLElement };
}

describe('VoicePicker (QA)', () => {
  beforeEach(() => {
    vi.spyOn(HTMLMediaElement.prototype, 'pause').mockImplementation(() => undefined);
    vi.spyOn(HTMLMediaElement.prototype, 'load').mockImplementation(() => undefined);
    vi.spyOn(HTMLMediaElement.prototype, 'play').mockImplementation(() => Promise.resolve());
  });

  afterEach(() => {
    TestBed.resetTestingModule();
    vi.restoreAllMocks();
  });

  it('shows EmptyState (not a blank step) for 0 voices when not loading, and its action fires backToLanguage', async () => {
    const { fixture, host, root } = await setupPicker([]);
    expect(root.querySelector('nts-empty-state')).not.toBeNull();
    expect(root.querySelector('mat-radio-group')).toBeNull();
    root.querySelector<HTMLButtonElement>('nts-empty-state button')?.click();
    fixture.detectChanges();
    expect(host.backClicks).toBe(1);
  });

  it('shows no EmptyState while the first load is in flight', async () => {
    const { fixture, host, root } = await setupPicker([]);
    host.loading.set(true);
    fixture.detectChanges();
    await fixture.whenStable();
    expect(root.querySelector('nts-empty-state')).toBeNull();
  });

  it('has no range input, no speed/rate control and no playbackRate hint in the DOM, but shows the rate hint text', async () => {
    const { root } = await setupPicker([voice(), voice({ id: 'v2', displayName: 'Minh', previewUrl: '/media/m.mp3' })]);
    expect(root.querySelector('input[type="range"]')).toBeNull();
    expect(root.querySelector('select')).toBeNull();
    expect(root.innerHTML).not.toMatch(/playbackRate/i);
    expect(root.textContent).toContain('Tốc độ đọc được cố định');
  });

  it('renders an unknown gender raw and drops an unknown tag from the chips', async () => {
    const { root } = await setupPicker([voice({ gender: 'robot', tags: ['warm', 'bogus' as never] })]);
    const text = root.textContent ?? '';
    expect(text).toContain('robot');
    expect(text).toContain('Ấm');
    expect(text).not.toContain('bogus');
    expect(root.querySelectorAll('.voice-picker__tag').length).toBe(1);
  });

  it('renders no chip for a voice whose tag list is empty', async () => {
    const { root } = await setupPicker([voice({ tags: [] }), voice({ id: 'v2', displayName: 'Minh', tags: ['calm'] })]);
    const chips = Array.from(root.querySelectorAll('.voice-picker__tag')).map((chip) => chip.textContent?.trim());
    expect(chips).toEqual(['Điềm tĩnh']);
    expect(root.querySelectorAll('mat-radio-button').length).toBe(2);
  });

  it('does not emit selected when the already-selected voice is clicked again', async () => {
    const { fixture, host, root } = await setupPicker([voice(), voice({ id: 'v2', displayName: 'Minh' })]);
    const firstInput = () => root.querySelectorAll<HTMLInputElement>('input[type="radio"]')[0]!;
    firstInput().click();
    fixture.detectChanges();
    await fixture.whenStable();
    expect(host.selects).toEqual(['v1']);
    firstInput().click();
    fixture.detectChanges();
    await fixture.whenStable();
    expect(host.selects).toEqual(['v1']);
  });

  it('keeps the preview button disabled and creates no <audio> for an unsafe previewUrl smuggled past the mapper', async () => {
    const { fixture, root } = await setupPicker([voice({ previewUrl: 'javascript:alert(1)' })]);
    const button = root.querySelector<HTMLButtonElement>('nts-audio-preview-button button');
    expect(button?.disabled).toBe(true);
    button?.click();
    fixture.detectChanges();
    await fixture.whenStable();
    expect(root.querySelector('audio')).toBeNull();
  });

  it('starting a second preview stops the first (one aria-pressed button at a time) and destroy releases the audio', async () => {
    const { fixture, root } = await setupPicker([
      voice({ id: 'a', displayName: 'A', previewUrl: '/media/a.mp3' }),
      voice({ id: 'b', displayName: 'B', previewUrl: '/media/b.mp3' }),
    ]);
    const buttons = () => Array.from(root.querySelectorAll<HTMLButtonElement>('nts-audio-preview-button button'));
    for (const index of [0, 1]) {
      buttons()[index]!.click();
      fixture.detectChanges();
      await fixture.whenStable();
    }
    expect(buttons().filter((b) => b.getAttribute('aria-pressed') === 'true').length).toBe(1);
    expect(buttons()[1]!.getAttribute('aria-pressed')).toBe('true');
    const audios = Array.from(root.querySelectorAll('audio'));
    expect(audios.length).toBeGreaterThan(0);
    const loadSpies = audios.map((audio) => vi.spyOn(audio, 'load'));
    fixture.destroy();
    for (const spy of loadSpies) expect(spy).toHaveBeenCalled();
    for (const audio of audios) expect(audio.getAttribute('src')).toBeNull();
  });
});

describe('VoiceStepStore (QA)', () => {
  function appError(code: string, status: number, kind: AppError['kind']): AppError {
    return { code, status, kind, fieldErrors: {} };
  }

  function setupStore(): { store: VoiceStepStore; repo: FakeCatalogRepository } {
    const repo = new FakeCatalogRepository();
    TestBed.configureTestingModule({
      imports: [translations],
      providers: [
        ...ZONELESS_TEST_PROVIDERS,
        { provide: SessionApi, useValue: new FakeSessionApi() },
        { provide: CatalogRepository, useValue: repo },
        { provide: LiveAnnouncer, useValue: { announce: () => Promise.resolve() } },
        VoiceStepStore,
      ],
    });
    return { store: TestBed.inject(VoiceStepStore), repo };
  }

  afterEach(() => TestBed.resetTestingModule());

  it('rapid vi -> en -> vi: stale answers are discarded, only the last request lands, loading stays true until then', async () => {
    const { store, repo } = setupStore();
    repo.defer();
    const first = store.load('vi');
    const second = store.load('en');
    const third = store.load('vi');
    expect(repo.voicedCalls).toEqual(['vi', 'en', 'vi']);

    repo.resolveLanguage('en');
    await second;
    expect(store.voices()).toEqual([]);
    repo.resolveLanguage('vi'); // the oldest vi request (stale generation)
    await first;
    expect(store.voices()).toEqual([]);
    expect(store.loading()).toBe(true);

    repo.resolveLanguage('vi');
    await third;
    expect(store.voices().map((v) => v.id)).toEqual(['vi-quiet', 'vi-warm', 'vi-hue']);
    expect(store.language()).toBe('vi');
    expect(store.loading()).toBe(false);
    expect(store.error()).toBeNull();
  });

  it('retry after network_error recovers the list and clears the error', async () => {
    const { store, repo } = setupStore();
    repo.failWith(appError('network_error', 0, 'network'));
    await store.load('vi');
    expect(store.error()?.code).toBe('network_error');
    expect(store.voices()).toEqual([]);

    repo.failWith(null);
    await store.retry();
    expect(store.error()).toBeNull();
    expect(store.voices().length).toBe(3);
    expect(store.loading()).toBe(false);
  });

  it('a successful reload clears a previous rate-limit countdown', async () => {
    const { store, repo } = setupStore();
    repo.failWith({ ...appError('rate_limited', 429, 'rate_limit'), retryAfterSec: 7 });
    await store.load('vi');
    expect(store.retryAfterSec()).toBe(7);
    repo.failWith(null);
    await store.retry();
    expect(store.retryAfterSec()).toBeNull();
    expect(store.error()).toBeNull();
  });

  it('a 304 for a language never loaded leaves the list untouched and does not throw', async () => {
    const { store, repo } = setupStore();
    repo.answerNotModified();
    await store.load('en');
    expect(store.voices()).toEqual([]);
    expect(store.loading()).toBe(false);
  });

  it('select ignores a second select of the same id without changing state', async () => {
    const { store } = setupStore();
    await store.load('vi');
    store.select('vi-warm');
    store.select('vi-warm');
    expect(store.selectedId()).toBe('vi-warm');
    expect(store.compatible()).toBe(true);
  });
});
