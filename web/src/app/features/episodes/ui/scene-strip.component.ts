import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import type { Signal } from '@angular/core';
import type { SegmentAddress } from '@shared/util/segment-address';
import { TranslocoPipe, provideTranslocoScope } from '@jsverse/transloco';
import type { SceneSegmentViewModel, SegmentTexts } from './story-editor.view-model';

interface ScenePreview {
  readonly key: string;
  readonly labelKey: string;
  readonly labelIndex: number | null;
  readonly keyword: string;
  readonly address: SegmentAddress;
}

// Only the corresponding excerpt observes text edits; the strip observes scene structure.
@Component({
  selector: 'p[ntsSceneExcerpt]',
  template: '{{ text()() }}',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class SceneExcerptComponent {
  readonly text = input.required<Signal<string>>();
}

@Component({
  selector: 'nts-scene-strip',
  imports: [TranslocoPipe, SceneExcerptComponent],
  providers: [provideTranslocoScope('episodes')],
  templateUrl: './scene-strip.component.html',
  styleUrl: './scene-strip.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class SceneStripComponent {
  readonly segments = input.required<readonly SceneSegmentViewModel[]>();
  readonly segmentTexts = input.required<SegmentTexts>();

  protected readonly scenes = computed<readonly ScenePreview[]>(() => this.segments().flatMap((segment) =>
    segment.keywords.map((keyword, index) => ({
      key: `${segment.segKey}:${String(index)}`,
      labelKey: segment.labelKey,
      labelIndex: segment.labelIndex,
      keyword,
      address: segment.address,
    })),
  ));
}
