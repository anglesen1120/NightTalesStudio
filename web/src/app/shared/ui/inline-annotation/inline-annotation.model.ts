export interface InlineAnnotationViewModel {
  readonly id: string;
  readonly address: string | null;
  readonly ruleId: string;
  readonly severity: 'advisory' | 'blocking';
  readonly kind: 'objective' | 'taste';
  readonly messageKey: string;
  readonly evidence: string;
  readonly repairDirection: string | null;
  readonly addressLabelKey: string | null;
  readonly addressLabelIndex: number | null;
  readonly canGoTo: boolean;
}
