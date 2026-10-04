import { inject } from '@angular/core';

class SeriesStore {}

export class SeriesList {
  readonly store = inject(SeriesStore);
}
