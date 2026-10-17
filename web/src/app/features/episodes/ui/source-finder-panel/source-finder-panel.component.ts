import { ChangeDetectionStrategy, Component, computed, input, output } from '@angular/core';
import { FormControl, ReactiveFormsModule, Validators } from '@angular/forms';
import { TranslocoPipe } from '@jsverse/transloco';
import { EmptyState, LoadingState, RetryCountdown } from '@shared/ui';
import type { SourceCandidate } from '../../domain/source-candidate.model';

let nextPanelId = 0;

@Component({
  selector: 'nts-source-finder-panel',
  standalone: true,
  imports: [ReactiveFormsModule, TranslocoPipe, EmptyState, LoadingState, RetryCountdown],
  templateUrl: './source-finder-panel.component.html',
  styleUrl: './source-finder-panel.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class SourceFinderPanelComponent {
  readonly candidates = input<readonly SourceCandidate[]>([]);
  readonly running = input(false);
  readonly failedRun = input(false);
  readonly finding = input(false);
  readonly loading = input(false);
  readonly completed = input(false);
  readonly canFind = input(true);
  readonly busyId = input<string | null>(null);
  readonly errorKey = input<string | null>(null);
  readonly retryAfterSec = input<number | null>(null);
  readonly rowErrorKeys = input<ReadonlyMap<string, string>>(new Map());
  readonly rowRetryAfterSec = input<number | null>(null);
  readonly sourceLimitReached = input(false);
  readonly find = output<string | null>();
  readonly add = output<string>();
  readonly dismiss = output<string>();
  readonly retryElapsed = output<void>();
  readonly rowRetryElapsed = output<void>();
  readonly clearError = output<void>();
  readonly query = new FormControl('', { nonNullable: true, validators: [Validators.maxLength(300)] });
  readonly panelId = 'source-finder-' + ++nextPanelId;
  readonly empty = computed(() => this.completed() && !this.loading() && !this.running() && !this.finding() && !this.failedRun() && !this.errorKey() && !this.candidates().some(candidate => candidate.status === 'proposed'));
  readonly retryRowId = computed(() => this.candidates().find(candidate => this.rowErrorKeys().get(candidate.id) === 'errors.source_fetch_limit_reached')?.id ?? null);

  submit(): void {
    if (!this.canFind() || this.running() || this.finding() || this.retryAfterSec() !== null || this.query.invalid) return;
    this.find.emit(this.query.value.trim() || null);
  }

  addCandidate(id: string): void {
    if (this.busyId() === id || this.sourceLimitReached() || (this.retryRowId() === id && this.rowRetryAfterSec() !== null)) return;
    this.add.emit(id);
  }

  dismissCandidate(id: string): void {
    if (this.busyId() === id) return;
    this.dismiss.emit(id);
  }
}
