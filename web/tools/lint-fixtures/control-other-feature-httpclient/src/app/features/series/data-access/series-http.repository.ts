import { HttpClient } from '@angular/common/http';
import { inject } from '@angular/core';

export class SeriesHttpRepository {
  readonly http = inject(HttpClient);
}
