import { ChangeDetectionStrategy, Component, ElementRef, afterNextRender, computed, input, output, signal, viewChild, type OnChanges } from '@angular/core';
import { TranslocoPipe } from '@jsverse/transloco';
import { FormControl, FormGroup, ReactiveFormsModule, Validators } from '@angular/forms';
import { bindFieldErrors, clearServerErrors, type AppError } from '@core/errors';
import { CharCounter, RetryCountdown } from '@shared/ui';
import { NOTE_MAX_CHARS, isSafeHttpUrl } from '../../domain/research.model';
let nextFormId = 0;
@Component({ selector: 'nts-source-add-form', imports: [ReactiveFormsModule, TranslocoPipe, CharCounter, RetryCountdown], templateUrl: './source-add-form.component.html', styleUrl: './source-add-form.component.scss', changeDetection: ChangeDetectionStrategy.OnPush })
export class SourceAddFormComponent implements OnChanges {
  readonly busy = input(false);
  readonly writeBlocked = input(false);
  readonly canAdd = input(true);
  readonly canAddUrl = input(true);
  readonly errorKey = input<string | null>(null);
  readonly formError = input<AppError | null>(null);
  readonly retryAfterSec = input<number | null>(null);
  readonly retryWait = signal<number | null>(null);
  private readonly urlOnlyWait = signal(false);
  readonly retryBlocked = computed(() => this.retryWait() !== null && (!this.urlOnlyWait() || this.mode() === 'url'));
  readonly duplicateSourceId = input<string | null>(null);
  readonly focusOnEntry = input(false);
  readonly addUrl = output<string>();
  readonly addNote = output<string>();
  readonly edited = output<void>();
  readonly retryElapsed = output<void>();
  readonly mode = signal<'url' | 'note'>('url');
  readonly value = signal('');
  readonly submitted = signal(false);
  readonly max = NOTE_MAX_CHARS;
  readonly form = new FormGroup({
    url: new FormControl('', { nonNullable: true, validators: [control => Validators.required(control)] }),
    noteText: new FormControl('', { nonNullable: true, validators: [control => Validators.required(control), Validators.maxLength(NOTE_MAX_CHARS)] }),
  });
  readonly fieldId = 'research-source-field-' + ++nextFormId;
  readonly localError = computed(() => this.mode() === 'note' && this.value().length > this.max ? 'episodes.research.sources.noteTooLong' : this.submitted() && this.mode() === 'url' && !isSafeHttpUrl(this.value().trim()) ? 'errors.source_url_invalid' : null);
  readonly disabledReason = computed(() => this.writeBlocked() ? 'episodes.factSheet.waitForRetry' : !this.canAdd() ? 'episodes.research.sources.capReached' : this.mode() === 'url' && !this.canAddUrl() ? 'episodes.research.sources.dailyCap' : null);
  private readonly field = viewChild<ElementRef<HTMLInputElement | HTMLTextAreaElement>>('field');
  constructor() { this.syncForm(); afterNextRender(() => { if (this.focusOnEntry()) this.focus(); }); }
  focus(): void { this.field()?.nativeElement.focus(); }
  clear(): void { this.form.reset(); this.value.set(''); this.submitted.set(false); }
  choose(mode: 'url' | 'note'): void {
    if (this.busy()) return;
    this.mode.set(mode); this.clear(); this.syncForm(); this.edited.emit();
  }
  change(value: string): void { this.activeControl().setValue(value, { emitEvent: false }); this.value.set(value); this.submitted.set(false); clearServerErrors(this.form); this.edited.emit(); }
  activeControl(): FormControl<string> { return this.mode() === 'url' ? this.form.controls.url : this.form.controls.noteText; }
  ngOnChanges(): void {
    this.syncForm();
    const error = this.formError();
    if (error) bindFieldErrors(this.form, error); else clearServerErrors(this.form);
    const seconds = this.retryAfterSec();
    if (seconds !== null) {
      this.retryWait.set(seconds);
      if (error) this.urlOnlyWait.set(error.code === 'source_fetch_limit_reached');
    }
  }
  finishRetry(): void { this.retryWait.set(null); this.urlOnlyWait.set(false); this.retryElapsed.emit(); }
  private syncForm(): void {
    this.form.disable({ emitEvent: false });
    if (!this.busy()) this.activeControl().enable({ emitEvent: false });
  }
  changeEvent(event: Event): void {
    const target = event.target;
    if (target instanceof HTMLInputElement || target instanceof HTMLTextAreaElement) this.change(target.value);
  }
  submit(): void {
    if (this.busy() || this.disabledReason() || this.retryBlocked()) return;
    this.submitted.set(true);
    if (!this.value().trim() || this.localError()) return;
    const value = this.activeControl().getRawValue();
    if (this.mode() === 'url') this.addUrl.emit(value.trim()); else this.addNote.emit(value);
  }
}
