import { ChangeDetectionStrategy, Component, inject } from '@angular/core';

class EpisodeRepository {}

@Component({
  selector: 'app-episode-card',
  template: '',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class EpisodeCardComponent {
  readonly repository = inject(EpisodeRepository);
}
