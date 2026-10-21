export type LegalDocumentKind = 'tos' | 'privacy';

export interface LegalDocumentStatus {
  readonly document: LegalDocumentKind;
  readonly currentVersion: string;
  readonly acceptedVersion: string | null;
  readonly acceptedAt: string | null;
}

export interface LegalAcceptance {
  readonly documents: readonly LegalDocumentStatus[];
}
