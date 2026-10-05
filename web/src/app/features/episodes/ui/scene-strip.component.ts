import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import { TranslocoPipe, provideTranslocoScope } from '@jsverse/transloco';
import type { SegmentEditorViewModel } from './story-editor.view-model';

interface ScenePreview {
  readonly key: string;
  readonly labelKey: string;
  readonly labelIndex: number | null;
  readonly keyword: string;
  readonly text: string;
}

@Component({
  selector: 'nts-scene-strip',
  imports: [TranslocoPipe],
  providers: [provideTranslocoScope('episodes')],
  templateUrl: './scene-strip.component.html',
  styleUrl: './scene-strip.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class SceneStripComponent {
  readonly segments = input.required<readonly SegmentEditorViewModel[]>();

  protected readonly scenes = computed<readonly ScenePreview[]>(() => this.segments().flatMap((segment) =>
    segment.keywords.map((keyword, index) => ({
      key: `${segment.segKey}:${String(index)}`,
      labelKey: segment.labelKey,
      labelIndex: segment.labelIndex,
      keyword,
      text: segment.text,
    })),
  ));
}
