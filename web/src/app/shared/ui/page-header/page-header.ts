import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import { RouterLink } from '@angular/router';
import { TranslocoPipe } from '@jsverse/transloco';

@Component({
  selector: 'nts-page-header',
  imports: [RouterLink, TranslocoPipe],
  templateUrl: './page-header.html',
  styleUrl: './page-header.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class PageHeader {
  readonly title = input<string>();
  readonly titleKey = input<string>();
  readonly subtitleKey = input<string>();
  readonly backHref = input<string>();

  protected readonly safeBackHref = computed(() => {
    const href = this.backHref();
    return href !== undefined && href.startsWith('/') && !href.startsWith('//') && !href.includes('\\')
      ? href
      : undefined;
  });
}
