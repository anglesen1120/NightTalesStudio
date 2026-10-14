import { ChangeDetectionStrategy, Component, computed, input, output } from '@angular/core';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { TranslocoPipe } from '@jsverse/transloco';
import { IconAlert, IconCheck } from '@shared/ui';
import { isSafeHttpUrl, type ResearchSource } from '../../domain/research.model';
const SOURCE_ERROR_CODES: readonly string[] = ['source_url_invalid', 'source_domain_not_allowed', 'source_unreachable', 'source_too_large', 'source_blocked_by_robots', 'source_unsupported_type', 'source_no_text'];
@Component({ selector: 'nts-source-row', imports: [TranslocoPipe, MatProgressSpinnerModule, IconAlert, IconCheck], templateUrl: './source-row.component.html', styleUrl: './source-row.component.scss', changeDetection: ChangeDetectionStrategy.OnPush })
export class SourceRowComponent {
  readonly source = input.required<ResearchSource>();
  readonly busy = input(false);
  readonly retryBlocked = input(false);
  readonly duplicate = input(false);
  readonly retrySource = output<void>();
  readonly removeSource = output<void>();
  readonly safeUrl = computed(() => { const url = this.source().finalUrl ?? this.source().url; return isSafeHttpUrl(url) ? url : null; });
  readonly title = computed(() => this.source().title || this.source().registrableDomain || this.source().url || '');
  readonly failureKey = computed(() => { const code = this.source().errorCode; return code && SOURCE_ERROR_CODES.includes(code) ? 'episodes.sources.error.' + code : 'episodes.sources.error.unknown'; });
}
