export interface DiffRow {
  readonly kind: 'add' | 'remove' | 'change';
  readonly label: string;
  readonly before?: string;
  readonly after?: string;
}
