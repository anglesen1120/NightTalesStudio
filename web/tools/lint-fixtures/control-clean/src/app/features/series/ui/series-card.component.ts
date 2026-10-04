import { ChangeDetectionStrategy, Component, input } from '@angular/core';

@Component({
  selector: 'app-series-card',
  template: '',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class SeriesCardComponent {
  readonly title = input.required<string>();
}
