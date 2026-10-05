import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { firstValueFrom } from 'rxjs';
import { userAction } from '@core/http';
import type { LegalAcceptance } from '../domain/legal-acceptance.model';

const LEGAL_ACCEPTANCE_URL = '/api/v1/me/legal';

/** Current-user legal acceptance HTTP adapter. HTTP failures intentionally reach the application store unchanged. */
@Injectable({ providedIn: 'root' })
export class LegalAcceptanceRepository {
  private readonly http = inject(HttpClient);

  get(): Promise<LegalAcceptance> {
    return firstValueFrom(this.http.get<LegalAcceptance>(LEGAL_ACCEPTANCE_URL, { context: userAction() }));
  }

  accept(tos: string, privacy: string): Promise<LegalAcceptance> {
    return firstValueFrom(
      this.http.post<LegalAcceptance>(`${LEGAL_ACCEPTANCE_URL}/accept`, { tos, privacy }, { context: userAction() }),
    );
  }
}
