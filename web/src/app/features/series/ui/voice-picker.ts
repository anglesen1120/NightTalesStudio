import { ChangeDetectionStrategy, Component, computed, input, linkedSignal, output, signal } from '@angular/core';
import { MatRadioModule } from '@angular/material/radio';
import { TranslocoPipe, provideTranslocoScope } from '@jsverse/transloco';
import { EmptyState, ErrorState, LoadingState, RetryCountdown } from '@shared/ui';
import { toVoicePickerViewModel } from '../domain/voice-picker.view-model';
import type { Voice } from '../domain/voice.model';
import { AudioPreviewButton } from './audio-preview-button';

let nextVoicePickerId = 0;

/**
 * Voice step picker (10.4.2-T2, frames f-new-step3 / f-new-step3-empty / f-new-errors).
 * Presentational only (D2): no injected store or HttpClient; inputs/outputs only.
 *
 * - `fieldset`/`legend` + `mat-radio-group` give the radiogroup semantics with working arrow-key
 *   selection (the design's native radios; Material keeps the roving focus a11y-correct).
 * - One preview plays at a time: `activeId` is set when a button emits `started` and cleared on
 *   `stopped`; each row's `playing` is `activeId() === row.id`.
 * - Errors are the shared pattern (D9): `ErrorState` + Retry for `network_error`; `rate_limited`
 *   renders `RetryCountdown` and keeps Retry disabled until the countdown elapses.
 * - No rate control anywhere (04 CR-11): `series.voice.rateFixed` is a hint line only.
 */
@Component({
  selector: 'nts-voice-picker',
  imports: [
    MatRadioModule,
    TranslocoPipe,
    EmptyState,
    ErrorState,
    LoadingState,
    RetryCountdown,
    AudioPreviewButton,
  ],
  providers: [provideTranslocoScope('series', 'voices', 'errors')],
  templateUrl: './voice-picker.html',
  styleUrl: './voice-picker.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class VoicePicker {
  readonly voices = input.required<readonly Voice[]>();
  readonly selectedId = input<string | null>(null);
  readonly disabled = input(false);
  readonly loading = input(false);
  /** Pre-computed `errors.<code>` key for the step-level failure, or `null` when healthy. */
  readonly errorKey = input<string | null>(null);
  /** `Retry-After` seconds while `errorKey` is `errors.rate_limited`; `null` otherwise. */
  readonly retryAfterSec = input<number | null>(null);
  readonly traceId = input<string | null>(null);

  readonly selected = output<string>();
  readonly retry = output<void>();
  /** Fires when a `rate_limited` countdown reaches zero (parents may auto-retry or just unlock). */
  readonly retryAfterElapsed = output<void>();
  /** Empty-state action: return to the language step (f-new-step3-empty). */
  readonly backToLanguage = output<void>();

  protected readonly groupName = `nts-voice-group-${nextVoicePickerId++}`;
  protected readonly vm = computed(() => toVoicePickerViewModel(this.voices()));
  protected readonly activeId = signal<string | null>(null);
  protected readonly retryLocked = linkedSignal({ source: this.retryAfterSec, computation: () => true });

  protected onSelectionChange(id: string): void {
    if (this.disabled() || id === this.selectedId()) return;
    this.selected.emit(id);
  }

  /**
   * Radiogroup arrow keys (f-new-step3 a11y note). Material radio relies on the browser's native
   * radio-group navigation; the handler moves selection and focus explicitly (and `preventDefault`s
   * so a native implementation can never double-move) — identical in every environment.
   */
  protected onGroupKeydown(event: KeyboardEvent): void {
    const rows = this.vm().rows;
    const delta =
      event.key === 'ArrowDown' || event.key === 'ArrowRight'
        ? 1
        : event.key === 'ArrowUp' || event.key === 'ArrowLeft'
          ? -1
          : 0;
    if (delta === 0 || rows.length === 0 || this.disabled()) return;
    const radios = Array.from(
      (event.currentTarget as HTMLElement).querySelectorAll<HTMLInputElement>('input[type="radio"]'),
    );
    const focused = radios.indexOf(event.target as HTMLInputElement);
    if (focused < 0) return;
    event.preventDefault();
    const nextIndex = (focused + delta + rows.length) % rows.length;
    const next = rows[nextIndex];
    if (next === undefined || next.id === this.selectedId()) return;
    this.selected.emit(next.id);
    radios[nextIndex]?.focus();
  }

  protected onPreviewStarted(voiceId: string): void {
    this.activeId.set(voiceId);
  }

  protected onPreviewStopped(voiceId: string): void {
    if (this.activeId() === voiceId) this.activeId.set(null);
  }

  protected onRetry(): void {
    if (this.retryAfterSec() !== null && this.retryLocked()) return;
    this.retry.emit();
  }

  protected onRetryElapsed(): void {
    this.retryLocked.set(false);
    this.retryAfterElapsed.emit();
  }
}
