import { Injectable, computed, inject, signal } from '@angular/core';
import { AppErrorMapper, type AppError } from '@core/errors';
import { SessionStore } from '@core/session';
import { LegalAcceptanceRepository } from '../data-access/legal-acceptance.repository';
import type { LegalAcceptance, LegalDocumentKind } from '../domain/legal-acceptance.model';

const ALPHABETICAL_DOCUMENTS: readonly LegalDocumentKind[] = ['privacy', 'tos'];

export type LegalAcceptanceErrorKey = 'errors.legal_version_outdated' | 'account.legal.error.generic';

/** Session-bound state for the legal acceptance dialog. It never changes the projection before the server succeeds. */
@Injectable({ providedIn: 'root' })
export class LegalAcceptanceStore {
  private readonly repository = inject(LegalAcceptanceRepository);
  private readonly mapper = inject(AppErrorMapper);
  private readonly session = inject(SessionStore);

  private readonly acceptanceState = signal<LegalAcceptance | null>(null);
  private readonly pendingState = signal(false);
  private readonly serverMissingState = signal<readonly LegalDocumentKind[] | null>(null);
  private readonly errorKeyState = signal<LegalAcceptanceErrorKey | null>(null);

  private request = 0;
  private readonly sessionGeneration = signal(this.session.generation());
  private readonly belongsToSession = computed(() => this.session.generation() === this.sessionGeneration());

  readonly acceptance = computed(() => (this.belongsToSession() ? this.acceptanceState() : null));
  readonly pending = computed(() => this.belongsToSession() && this.pendingState());
  readonly errorKey = computed(() => (this.belongsToSession() ? this.errorKeyState() : null));
  readonly missing = computed<readonly LegalDocumentKind[]>(() => {
    if (!this.belongsToSession()) return [];
    const fromError = this.serverMissingState();
    if (fromError !== null) return fromError;
    const acceptance = this.acceptanceState();
    if (acceptance === null) return [];
    return ALPHABETICAL_DOCUMENTS.filter((kind) => {
      const status = acceptance.documents.find((document) => document.document === kind);
      return status === undefined || status.acceptedVersion !== status.currentVersion;
    });
  });

  async refresh(): Promise<boolean> {
    this.syncSession();
    if (this.pendingState()) return false;
    const request = ++this.request;
    const generation = this.sessionGeneration();
    this.pendingState.set(true);
    this.errorKeyState.set(null);
    this.serverMissingState.set(null);

    try {
      const acceptance = await this.repository.get();
      if (!this.isCurrent(request, generation)) return false;
      this.acceptanceState.set(acceptance);
      return true;
    } catch (cause) {
      if (!this.isCurrent(request, generation)) return false;
      this.applyError(this.mapper.from(cause));
      return false;
    } finally {
      if (this.isCurrent(request, generation)) this.pendingState.set(false);
    }
  }

  async accept(tos: string, privacy: string): Promise<boolean> {
    this.syncSession();
    if (this.pendingState()) return false;
    const request = ++this.request;
    const generation = this.sessionGeneration();
    this.pendingState.set(true);
    this.errorKeyState.set(null);
    this.serverMissingState.set(null);

    try {
      const acceptance = await this.repository.accept(tos, privacy);
      if (!this.isCurrent(request, generation)) return false;
      this.acceptanceState.set(acceptance);
      return true;
    } catch (cause) {
      if (!this.isCurrent(request, generation)) return false;
      const error = this.mapper.from(cause);
      this.applyError(error);
      if (error.code === 'legal_version_outdated') {
        await this.refreshAfterOutdated(request, generation);
      }
      return false;
    } finally {
      if (this.isCurrent(request, generation)) this.pendingState.set(false);
    }
  }

  private async refreshAfterOutdated(request: number, generation: number): Promise<void> {
    try {
      const acceptance = await this.repository.get();
      if (this.isCurrent(request, generation)) this.acceptanceState.set(acceptance);
    } catch (cause) {
      if (this.isCurrent(request, generation)) this.applyError(this.mapper.from(cause));
    }
  }

  private applyError(error: AppError): void {
    this.errorKeyState.set(
      error.code === 'legal_version_outdated' ? 'errors.legal_version_outdated' : 'account.legal.error.generic',
    );
    this.serverMissingState.set(missingDocuments(error));
  }

  private isCurrent(request: number, generation: number): boolean {
    if (this.session.generation() !== generation) {
      this.syncSession();
      return false;
    }
    return this.request === request;
  }

  private syncSession(): void {
    const generation = this.session.generation();
    if (generation === this.sessionGeneration()) return;
    this.sessionGeneration.set(generation);
    this.request += 1;
    this.acceptanceState.set(null);
    this.pendingState.set(false);
    this.serverMissingState.set(null);
    this.errorKeyState.set(null);
  }
}

function missingDocuments(error: AppError): readonly LegalDocumentKind[] | null {
  const documents = error.fieldErrors['documents'];
  if (documents === undefined) return null;
  const included = new Set<LegalDocumentKind>();
  for (const document of documents) {
    if (document === 'privacy' || document === 'tos') included.add(document);
  }
  return ALPHABETICAL_DOCUMENTS.filter((document) => included.has(document));
}
