import { ChangeDetectionStrategy, Component } from '@angular/core';
import { of } from 'rxjs';

@Component({
  selector: 'app-series-list',
  template: '',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class SeriesListComponent {
  load(): void {
    of(1).subscribe();
  }
}
