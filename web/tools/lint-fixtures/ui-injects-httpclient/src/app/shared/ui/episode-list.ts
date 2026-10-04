import { HttpClient } from '@angular/common/http';
import { inject } from '@angular/core';

export class EpisodeList {
  readonly http = inject(HttpClient);
}
