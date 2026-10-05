import { ChangeDetectionStrategy, Component, signal } from '@angular/core';
import { TestBed, type ComponentFixture } from '@angular/core/testing';
import { TranslocoService, TranslocoTestingModule } from '@jsverse/transloco';
import { afterEach, beforeEach, describe, expect, it, vi, type MockInstance } from 'vitest';
import { ZONELESS_TEST_PROVIDERS } from '../../../testing/providers';
import type { Voice } from '../domain/voice.model';
import { VoicePicker } from './voice-picker';

// Inline scope translations (the real files are delivered alongside; specs never read them).
const coreVi = { common: { back: 'Quay lại' } };
const coreEn = { common: { back: 'Back' } };
const errorsVi = {
  action: { retry: 'Thử lại' },
  network_error: 'Không kết nối được tới máy chủ. Hãy kiểm tra mạng và thử lại.',
  rate_limited: 'Quá nhiều yêu cầu. Hãy đợi một chút.',
  retryIn: 'Bạn có thể thử lại sau {{seconds}} giây',
  retryNow: 'Bạn có thể thử lại ngay',
};
const errorsEn = {
  action: { retry: 'Try again' },
  network_error: 'Could not reach the server. Check your connection and try again.',
  rate_limited: 'Too many requests. Please wait a moment.',
  retryIn: 'You can retry in {{seconds}} seconds',
  retryNow: 'You can retry now',
};
const seriesVi = {
  field: { voice: 'Giọng đọc' },
  voice: {
    preview: 'Nghe thử',
    stop: 'Dừng nghe',
    previewUnavailable: 'Chưa có bản nghe thử cho giọng này',
    previewFailed: 'Không phát được bản nghe thử',
    rateFixed: 'Tốc độ đọc được cố định',
    empty: 'Chưa có giọng đọc cho ngôn ngữ này',
    gender: { male: 'Nam', female: 'Nữ', neutral: 'Trung tính' },
  },
};
const seriesEn = {
  field: { voice: 'Narrator' },
  voice: {
    preview: 'Play preview',
    stop: 'Stop preview',
    previewUnavailable: 'No preview is available for this voice',
    previewFailed: 'The preview could not be played',
    rateFixed: 'The speaking rate is fixed',
    empty: 'No voices are available for this language',
    gender: { male: 'Male', female: 'Female', neutral: 'Neutral' },
  },
};
const voicesVi = {
  tag: {
    storyteller: 'Kể chuyện',
    natural: 'Tự nhiên',
    news: 'Tin tức',
    deep: 'Trầm',
    warm: 'Ấm',
    calm: 'Điềm tĩnh',
    bright: 'Sáng',
    whisper: 'Thì thầm',
    northern: 'Miền Bắc',
    central: 'Miền Trung',
    southern: 'Miền Nam',
  },
};
const voicesEn = {
  tag: {
    storyteller: 'Storyteller',
    natural: 'Natural',
    news: 'News',
    deep: 'Deep',
    warm: 'Warm',
    calm: 'Calm',
    bright: 'Bright',
    whisper: 'Whisper',
    northern: 'Northern',
    central: 'Central',
    southern: 'Southern',
  },
};

const translations = TranslocoTestingModule.forRoot({
  langs: {
    vi: coreVi,
    en: coreEn,
    'series/vi': seriesVi,
    'series/en': seriesEn,
    'voices/vi': voicesVi,
    'voices/en': voicesEn,
    'errors/vi': errorsVi,
    'errors/en': errorsEn,
  },
  translocoConfig: {
    availableLangs: ['vi', 'en'],
    defaultLang: 'vi',
    fallbackLang: 'vi',
    reRenderOnLangChange: true,
  },
  preloadLangs: true,
});

const voices: readonly Voice[] = [
  {
    id: 'minh-anh',
    displayName: 'Minh Anh',
    language: 'vi',
    gender: 'female',
    tags: ['storyteller', 'warm'],
    previewUrl: '/media/minh-anh.v1.mp3',
  },
  {
    id: 'hoang-nam',
    displayName: 'Hoàng Nam',
    language: 'vi',
    gender: 'male',
    tags: ['deep', 'northern'],
    previewUrl: null,
  },
];

@Component({
  imports: [VoicePicker],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `<nts-voice-picker
    [voices]="voices()"
    [selectedId]="selectedId()"
    [disabled]="disabled()"
    [loading]="loading()"
    [errorKey]="errorKey()"
    [retryAfterSec]="retryAfterSec()"
    (selected)="selectedId.set($event); lastSelected = $event; selects = selects + 1"
    (retry)="retries = retries + 1"
    (retryAfterElapsed)="elapsed = elapsed + 1"
    (backToLanguage)="backClicks = backClicks + 1"
  />`,
})
class VoicePickerHost {
  readonly voices = signal<readonly Voice[]>(voices);
  readonly selectedId = signal<string | null>(null);
  readonly disabled = signal(false);
  readonly loading = signal(false);
  readonly errorKey = signal<string | null>(null);
  readonly retryAfterSec = signal<number | null>(null);
  lastSelected: string | null = null;
  selects = 0;
  retries = 0;
  elapsed = 0;
  backClicks = 0;
}

let pauseSpy: MockInstance<HTMLMediaElement['pause']>;

beforeEach(() => {
  pauseSpy = vi.spyOn(HTMLMediaElement.prototype, 'pause').mockImplementation(() => undefined);
  vi.spyOn(HTMLMediaElement.prototype, 'load').mockImplementation(() => undefined);
  vi.spyOn(HTMLMediaElement.prototype, 'play').mockImplementation(() => Promise.resolve());
});

afterEach(() => {
  TestBed.resetTestingModule();
  vi.restoreAllMocks();
  vi.useRealTimers();
});

async function setup(): Promise<{ fixture: ComponentFixture<VoicePickerHost>; host: VoicePickerHost; root: HTMLElement }> {
  await TestBed.configureTestingModule({
    imports: [VoicePickerHost, translations],
    providers: ZONELESS_TEST_PROVIDERS,
  }).compileComponents();
  const fixture = TestBed.createComponent(VoicePickerHost);
  fixture.detectChanges();
  await fixture.whenStable();
  return { fixture, host: fixture.componentInstance, root: fixture.nativeElement as HTMLElement };
}

function radioInputs(root: HTMLElement): HTMLInputElement[] {
  return Array.from(root.querySelectorAll<HTMLInputElement>('input[type="radio"]'));
}

function previewButtons(root: HTMLElement): HTMLButtonElement[] {
  return Array.from(root.querySelectorAll<HTMLButtonElement>('nts-audio-preview-button button'));
}


describe('VoicePicker', () => {
  it('RendersOneRowPerVoice', async () => {
    const { root } = await setup();
    const rows = root.querySelectorAll('mat-radio-button');
    expect(rows.length).toBe(2);
    const text = root.textContent ?? '';
    expect(text).toContain('Minh Anh');
    expect(text).toContain('Hoàng Nam');
    // f-new-step3: fieldset + legend carry the section name.
    expect(root.querySelector('fieldset')).not.toBeNull();
    expect(root.querySelector('legend')?.textContent).toContain('Giọng đọc');
    // Gender text is translated (series.voice.gender.*).
    expect(text).toContain('Nữ');
    expect(text).toContain('Nam');
  });

  it('RendersTagsAsTranslatedKeys', async () => {
    const { root } = await setup();
    const text = root.textContent ?? '';
    // Voices scope renders verbatim tag copy, never the raw wire tag.
    expect(text).toContain('Kể chuyện');
    expect(text).toContain('Ấm');
    expect(text).toContain('Trầm');
    expect(text).toContain('Miền Bắc');
    expect(text).not.toContain('voices.tag.');
  });

  it('DuplicateTags_RenderOneChip', async () => {
    const { fixture, host, root } = await setup();
    host.voices.set([{ ...voices[0]!, tags: ['warm', 'warm'] }]);
    fixture.detectChanges();
    await fixture.whenStable();

    const chips = root.querySelectorAll('.voice-picker__tag');
    expect(chips.length).toBe(1);
    expect(chips[0]?.textContent).toBe('Ấm');
  });

  it('NoRateControl', async () => {
    const { root } = await setup();
    expect(root.querySelector('input[type="range"]')).toBeNull();
    expect(root.querySelector('select')).toBeNull();
    // The fixed rate is a hint line only (04 CR-11), not a control.
    expect(root.textContent).toContain('Tốc độ đọc được cố định');
  });

  it('Disabled_BlocksSelection', async () => {
    const { fixture, host, root } = await setup();
    host.disabled.set(true);
    fixture.detectChanges();
    await fixture.whenStable();

    for (const input of radioInputs(root)) expect(input.disabled).toBe(true);
    for (const btn of previewButtons(root)) expect(btn.disabled).toBe(true);
    radioInputs(root)[0]!.dispatchEvent(new KeyboardEvent('keydown', { key: 'ArrowDown', bubbles: true, cancelable: true }));
    fixture.detectChanges();
    await fixture.whenStable();
    expect(host.selects).toBe(0);
    expect(host.lastSelected).toBeNull();
  });

  it('Selected_EmitsOnce', async () => {
    const { fixture, host, root } = await setup();
    const first = radioInputs(root)[0]!;
    first.click();
    fixture.detectChanges();
    await fixture.whenStable();
    expect(host.selects).toBe(1);
    expect(host.lastSelected).toBe('minh-anh');

    // Clicking the already-checked radio changes nothing.
    first.click();
    fixture.detectChanges();
    await fixture.whenStable();
    expect(host.selects).toBe(1);
  });

  it('Keyboard_ArrowKeys_ChangeSelection', async () => {
    const { fixture, host, root } = await setup();
    host.selectedId.set('minh-anh');
    fixture.detectChanges();
    await fixture.whenStable();

    const first = radioInputs(root)[0]!;
    first.dispatchEvent(new KeyboardEvent('keydown', { key: 'ArrowDown', bubbles: true, cancelable: true }));
    fixture.detectChanges();
    await fixture.whenStable();
    expect(host.lastSelected).toBe('hoang-nam');

    radioInputs(root)[1]!.dispatchEvent(
      new KeyboardEvent('keydown', { key: 'ArrowUp', bubbles: true, cancelable: true }),
    );
    fixture.detectChanges();
    await fixture.whenStable();
    expect(host.lastSelected).toBe('minh-anh');
  });

  it('Keyboard_ArrowDownOnPreview_DoesNotSelectOrMoveFocus', async () => {
    const { fixture, host, root } = await setup();
    host.selectedId.set('minh-anh');
    fixture.detectChanges();
    await fixture.whenStable();

    const preview = previewButtons(root)[0]!;
    preview.focus();
    expect(document.activeElement).toBe(preview);
    const event = new KeyboardEvent('keydown', { key: 'ArrowDown', bubbles: true, cancelable: true });
    preview.dispatchEvent(event);
    fixture.detectChanges();
    await fixture.whenStable();

    expect(host.selects).toBe(0);
    expect(host.selectedId()).toBe('minh-anh');
    expect(document.activeElement).toBe(preview);
    expect(event.defaultPrevented).toBe(false);
  });

  it('UnknownTag_IsDropped', async () => {
    const { fixture, host, root } = await setup();
    host.voices.set([
      {
        id: 'x',
        displayName: 'Wire Voice',
        language: 'vi',
        gender: 'neutral',
        tags: ['calm', 'smuggled-tag' as Voice['tags'][number]],
        previewUrl: null,
      },
    ]);
    fixture.detectChanges();
    await fixture.whenStable();

    const text = root.textContent ?? '';
    expect(text).toContain('Điềm tĩnh');
    expect(text).not.toContain('smuggled-tag');
    expect(text).not.toContain('voices.tag.');
  });

  it('OnlyOnePlayerAtATime', async () => {
    const { fixture, host, root } = await setup();
    host.voices.set([
      { ...voices[0]! },
      { ...voices[1]!, previewUrl: '/media/nam.v1.mp3' },
    ]);
    fixture.detectChanges();
    await fixture.whenStable();

    pauseSpy.mockClear();

    const [firstBtn, secondBtn] = previewButtons(root);
    firstBtn!.click();
    fixture.detectChanges();
    await fixture.whenStable();
    expect(firstBtn!.getAttribute('aria-pressed')).toBe('true');

    // Starting the second preview releases the first — exactly one pressed control.
    secondBtn!.click();
    fixture.detectChanges();
    await fixture.whenStable();
    expect(firstBtn!.getAttribute('aria-pressed')).toBe('false');
    expect(secondBtn!.getAttribute('aria-pressed')).toBe('true');
    expect(pauseSpy).toHaveBeenCalled();
  });

  it('translates the series and voices keys in vi and en', async () => {
    const { fixture, root } = await setup();
    const transloco = TestBed.inject(TranslocoService);
    expect((root.textContent ?? '')).toContain('Giọng đọc');
    expect((root.textContent ?? '')).toContain('Kể chuyện');

    transloco.setActiveLang('en');
    fixture.detectChanges();
    await fixture.whenStable();
    const text = root.textContent ?? '';
    expect(text).toContain('Narrator');
    expect(text).toContain('Storyteller');
    expect(text).toContain('The speaking rate is fixed');
  });

  it('renders the empty state with a back-to-language action when the catalog is empty', async () => {
    const { fixture, host, root } = await setup();
    host.voices.set([]);
    fixture.detectChanges();
    await fixture.whenStable();

    expect(root.querySelector('nts-empty-state')).not.toBeNull();
    expect(root.querySelector('mat-radio-group')).toBeNull();
    const action = Array.from(root.querySelectorAll('button')).find((b) =>
      (b.textContent ?? '').includes('Quay lại'),
    );
    expect(action).toBeDefined();
    action?.click();
    fixture.detectChanges();
    await fixture.whenStable();
    expect(host.backClicks).toBe(1);
  });

  it('shows ErrorState with Retry for a network error and emits retry', async () => {
    const { fixture, host, root } = await setup();
    host.errorKey.set('errors.network_error');
    fixture.detectChanges();
    await fixture.whenStable();

    expect(root.querySelector('nts-error-state')).not.toBeNull();
    expect(root.textContent).toContain('Không kết nối được tới máy chủ');
    // The list stays visible — the rest of the form is never blocked.
    expect(radioInputs(root).length).toBe(2);

    const retryBtn = root.querySelector('nts-error-state button');
    (retryBtn as HTMLButtonElement | null)?.click();
    fixture.detectChanges();
    await fixture.whenStable();
    expect(host.retries).toBe(1);
  });

  it('RateLimited_ZeroRetryAfter_EnablesRetryAndEmits', async () => {
    const { fixture, host, root } = await setup();
    host.errorKey.set('errors.rate_limited');
    host.retryAfterSec.set(0);
    fixture.detectChanges();
    await fixture.whenStable();

    expect(root.querySelector('nts-retry-countdown')).not.toBeNull();
    expect(host.elapsed).toBe(1);
    const retryBtn = root.querySelector<HTMLButtonElement>('.voice-picker__retry');
    expect(retryBtn).not.toBeNull();
    expect(retryBtn?.disabled).toBe(false);
    retryBtn?.click();
    expect(host.retries).toBe(1);
  });

  it('rate_limited shows RetryCountdown and keeps Retry disabled until elapsed', async () => {
    vi.useFakeTimers();
    const { fixture, host, root } = await setup();
    host.errorKey.set('errors.rate_limited');
    host.retryAfterSec.set(30);
    fixture.detectChanges();
    await fixture.whenStable();

    expect(root.querySelector('nts-retry-countdown')).not.toBeNull();
    const retryBtn = root.querySelector<HTMLButtonElement>('.voice-picker__retry');
    expect(retryBtn?.disabled).toBe(true);
    retryBtn?.click();
    expect(host.retries).toBe(0);

    // The countdown elapses; Retry unlocks and `retryAfterElapsed` fires once.
    await vi.advanceTimersByTimeAsync(30_000);
    fixture.detectChanges();
    await fixture.whenStable();
    expect(host.elapsed).toBe(1);
    expect(retryBtn?.disabled).toBe(false);
    retryBtn?.click();
    expect(host.retries).toBe(1);
  });
});
