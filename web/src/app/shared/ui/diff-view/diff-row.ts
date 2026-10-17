export interface DiffRow {
  readonly kind: 'add' | 'remove' | 'change';
  readonly label: string;
  readonly before?: string;
  readonly after?: string;
  /** Optional secondary line (e.g. the reason of a proposed bible op); rendered as text. */
  readonly reason?: string;
}
