import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import { TranslocoPipe } from '@jsverse/transloco';
import { IconAlert } from '@shared/ui';

const NOTE_KEYS: Readonly<Record<string, string>> = {
  subtitle_sync_suspect: 'episodes.render.quality.subtitleSyncSuspect',
  subtitle_cues_long: 'episodes.render.quality.subtitleCuesLong',
  music_none: 'episodes.render.quality.musicNone',
  scenes_trimmed: 'episodes.render.quality.scenesTrimmed',
  scenes_sparse: 'episodes.render.quality.scenesSparse',
};
let nextNotesId = 0;

@Component({
  selector: 'nts-quality-notes',
  imports: [TranslocoPipe, IconAlert],
  templateUrl: './quality-notes.component.html',
  styleUrl: './quality-notes.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class QualityNotesComponent {
  readonly codes = input<readonly string[]>([]);
  protected readonly headingId = `nts-quality-notes-${++nextNotesId}`;
  protected readonly keys = computed(() => [...new Set(this.codes())].flatMap((code) => Object.hasOwn(NOTE_KEYS, code) ? [NOTE_KEYS[code]!] : []));
}
