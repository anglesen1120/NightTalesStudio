import { DecimalPipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, computed, input, output, signal } from '@angular/core';
import { TranslocoPipe, provideTranslocoScope } from '@jsverse/transloco';
import { parseAddress, type SegmentAddress } from '@shared/util/segment-address';
import type { ReviewCounts, ReviewIssue } from '../domain/audit.model';
import type { ReviewScores, SimilarityHighlightViewModel } from './review-panel.view-model';

@Component({
  selector: 'nts-review-panel',
  imports: [DecimalPipe, TranslocoPipe],
  providers: [provideTranslocoScope('episodes', 'quality', 'story')],
  templateUrl: './review-panel.component.html',
  styleUrl: './review-panel.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class ReviewPanelComponent {
  readonly counts = input<ReviewCounts>({ blocking: 0, advisory: 0 });
  readonly scores = input<ReviewScores | null>(null);
  readonly stale = input(false);
  readonly whyIssues = input<readonly ReviewIssue[]>([]);
  readonly driftNote = input(false);
  readonly similarityRows = input<readonly SimilarityHighlightViewModel[]>([]);
  readonly similarityCorpus = input<string | null>(null);
  readonly similarityScore = input<number | null>(null);
  readonly similarityUnavailable = input(false);
  readonly hasAudit = input(false);
  readonly partial = input(false);
  readonly idPrefix = input('story-review-audit');
  readonly focusRequested = output<SegmentAddress>();

  protected readonly whyExpanded = signal(false);
  protected readonly whyId = computed(() => `${this.idPrefix()}-why`);
  protected readonly showSimilarity = computed(() => this.similarityUnavailable() || this.similarityRows().length > 0 || this.similarityScore() !== null);
  protected readonly scoreDimensions = [
    { key: 'hook', label: 'episodes.audit.score.hook' },
    { key: 'pacing', label: 'episodes.audit.score.pacing' },
    { key: 'atmosphere', label: 'episodes.audit.score.atmosphere' },
    { key: 'natural_language', label: 'episodes.audit.score.natural_language' },
    { key: 'originality', label: 'episodes.audit.score.originality' },
    { key: 'factual_care', label: 'episodes.audit.score.factual_care' },
    { key: 'length_fit', label: 'episodes.audit.score.length_fit' },
  ] as const;

  protected openSegment(address: SegmentAddress): void {
    if (address !== 'meta' && parseAddress(address) !== null) this.focusRequested.emit(address);
  }
}
