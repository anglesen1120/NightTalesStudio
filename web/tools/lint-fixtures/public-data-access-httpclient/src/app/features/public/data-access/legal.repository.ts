import { HttpClient } from '@angular/common/http';
import { inject } from '@angular/core';

export class RemoteLegalRepository {
  readonly http = inject(HttpClient);
}
