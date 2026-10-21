import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import { RouterLink } from '@angular/router';
import { TranslocoPipe, provideTranslocoScope } from '@jsverse/transloco';
import { IconAlert } from '@shared/ui';
import { formatAddress, parseAddress } from '@shared/util/segment-address';
import type { SkippedScene } from '../../domain/render.model';

const REASON_KEYS: Readonly<Record<string, string>> = {
  moderated: 'episodes.render.images.reason.moderated',
  rate_limited: 'episodes.render.images.reason.rate_limited',
  relay_rejected: 'episodes.render.images.reason.relay_rejected',
  timeout: 'episodes.render.images.reason.timeout',
  upstream_error: 'episodes.render.images.reason.upstream_error',
};

@Component({
  selector: 'nts-skipped-scenes',
  imports: [RouterLink, TranslocoPipe, IconAlert],
  providers: [provideTranslocoScope('episodes')],
  templateUrl: './skipped-scenes.component.html',
  styleUrl: './skipped-scenes.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class SkippedScenesComponent {
  readonly skipped = input.required<readonly SkippedScene[]>();
  readonly episodeId = input.required<string>();
  readonly headingKey = input.required<string>();

  protected readonly titleKey = computed(() => this.headingKey().startsWith('episodes.') ? this.headingKey() : `episodes.${this.headingKey()}`);
  protected readonly rows = computed(() => this.skipped().map((scene, index) => {
    const parsed = parseAddress(scene.segment);
    return {
      trackKey: `${index}:${scene.segment}`,
      count: index + 1,
      segment: scene.segment,
      address: parsed ? formatAddress(parsed.kind, parsed.index ?? undefined) : null,
      reasonKey: Object.hasOwn(REASON_KEYS, scene.reason) ? REASON_KEYS[scene.reason]! : 'episodes.render.images.reason.unknown',
      moderated: scene.reason === 'moderated',
    };
  }));
}
