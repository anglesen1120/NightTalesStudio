import { ChangeDetectionStrategy, Component, inject } from '@angular/core';

class SeriesStore {}

@Component({
  selector: 'app-series-page',
  template: '',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class SeriesPageComponent {
  readonly store = inject(SeriesStore);
}
