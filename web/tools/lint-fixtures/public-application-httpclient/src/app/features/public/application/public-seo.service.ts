import { HttpClient } from '@angular/common/http';
import { inject } from '@angular/core';

export class PublicSeoService {
  readonly http = inject(HttpClient);
}
