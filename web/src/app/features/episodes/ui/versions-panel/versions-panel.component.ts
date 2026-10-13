import { ChangeDetectionStrategy, Component, ElementRef, afterNextRender, computed, inject, input, output, signal, viewChild } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { TranslocoPipe, TranslocoService, provideTranslocoScope } from '@jsverse/transloco';
import { AppErrorMapper, type AppError } from '@core/errors';
import { EmptyState, ErrorState, FormErrorAlert, IconCheck, IconClock, LoadingState } from '@shared/ui';
import { DiffViewComponent } from '@shared/ui/diff-view/diff-view.component';
import type { WordDiff } from '@shared/util/text-diff';
import type { StoryVersionDetail, StoryVersionSummary } from '../../domain/story-version.model';
import { episodeDateFormatter } from '../episode-list.view-model';

@Component({
  selector: 'nts-versions-panel',
  imports: [TranslocoPipe, EmptyState, ErrorState, FormErrorAlert, IconCheck, IconClock, LoadingState, DiffViewComponent],
  providers: [provideTranslocoScope('episodes', 'errors')],
  templateUrl: './versions-panel.component.html',
  styleUrl: './versions-panel.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { '(keydown.escape)': 'onEscape($event)' },
})
export class VersionsPanelComponent {
  readonly versions = input<readonly StoryVersionSummary[]>([]);
  readonly loaded = input(false);
  readonly empty = input(false);
  readonly leftId = input<string | null>(null);
  readonly rightId = input<string | null>(null);
  readonly left = input<StoryVersionDetail | null>(null);
  readonly right = input<StoryVersionDetail | null>(null);
  readonly diff = input<WordDiff | null>(null);
  readonly sameSelected = input(false);
  readonly loading = input(false);
  readonly loadingSide = input<'left' | 'right' | null>(null);
  readonly sideError = input<{ side: 'left' | 'right'; error: AppError } | null>(null);
  readonly error = input<AppError | null>(null);
  readonly restoring = input(false);
  readonly canRestore = input(false);
  readonly needsReopen = input(false);
  readonly restoreError = input<AppError | null>(null);
  readonly selectVersion = output<{ versionId: string; side: 'left' | 'right' }>();
  readonly retryList = output<void>();
  readonly retrySide = output<'left' | 'right'>();
  readonly restore = output<void>();
  readonly closed = output<void>();

  protected readonly activeSide = signal<'left' | 'right'>('left');
  private readonly mapper = inject(AppErrorMapper);
  private readonly transloco = inject(TranslocoService);
  private readonly language = toSignal(this.transloco.langChanges$, { initialValue: this.transloco.getActiveLang() });
  private readonly heading = viewChild<ElementRef<HTMLHeadingElement>>('heading');
  private readonly renderedPair = signal<{ left: StoryVersionDetail; right: StoryVersionDetail; diff: WordDiff } | null>(null);
  protected readonly summary = computed(() => {
    const rendered = this.renderedPair();
    if (this.sameSelected() || this.diff()?.truncated) return null;
    const left = this.left(), right = this.right();
    return rendered !== null && left !== null && right !== null &&
      rendered.left.versionId === left.versionId && rendered.left.body === left.body &&
      rendered.right.versionId === right.versionId && rendered.right.body === right.body ? rendered.diff : null;
  });
  protected readonly errorKey = computed(() => {
    const error = this.error();
    return error === null ? 'errors.unknown_error' : this.mapper.messageKey(error);
  });
  protected readonly sideErrorKey = computed(() => {
    const error = this.sideError();
    return error === null ? 'errors.unknown_error' : this.mapper.messageKey(error.error);
  });
  protected readonly rows = computed(() => {
    const dateFormat = episodeDateFormatter(this.language());
    const numberFormat = new Intl.NumberFormat(this.language() === 'en' ? 'en-US' : 'vi-VN');
    const percentFormat = new Intl.NumberFormat(this.language() === 'en' ? 'en-US' : 'vi-VN', { style: 'percent', maximumFractionDigits: 1 });
    return this.versions().map((version) => {
      const date = new Date(version.createdAt);
      return { version, date: Number.isNaN(date.getTime()) ? '—' : dateFormat.format(date), words: numberFormat.format(version.wordCount), similarity: version.similarityScore === null ? '—' : percentFormat.format(version.similarityScore) };
    });
  });

  constructor() {
    afterNextRender(() => this.heading()?.nativeElement.focus());
  }

  protected choose(versionId: string): void {
    this.selectVersion.emit({ versionId, side: this.activeSide() });
  }

  protected onRendered(diff: WordDiff): void {
    const left = this.left(), right = this.right();
    if (left !== null && right !== null) this.renderedPair.set({ left, right, diff });
  }

  protected close(): void {
    if (!this.restoring()) this.closed.emit();
  }

  protected onEscape(event: Event): void {
    event.stopPropagation();
    event.preventDefault();
    this.close();
  }

  // ReopenActionSeam (10.5.6-T1): needsReopen is message-only until that owner's real action lands.
  // This presentational component deliberately exposes no reopen output and no inert button.
}
