import { ChangeDetectionStrategy, Component, computed, input, output } from '@angular/core';
import { TranslocoPipe } from '@jsverse/transloco';
import type { AppError } from '@core/errors';
import { EmptyState, LoadingState } from '@shared/ui';
import type { Fact, FactSheet, ResearchSource } from '../../domain/research.model';
import { FactRowComponent, type FactPatch } from '../fact-row/fact-row.component';
@Component({ selector: 'nts-fact-sheet-panel', imports: [TranslocoPipe, EmptyState, LoadingState, FactRowComponent], templateUrl: './fact-sheet-panel.component.html', styleUrl: './fact-sheet-panel.component.scss', changeDetection: ChangeDetectionStrategy.OnPush })
export class FactSheetPanelComponent {
  readonly sheet = input<FactSheet | null>(null);
  readonly facts = input<readonly (Fact & { readonly clientKey: string })[]>([]);
  readonly sources = input<readonly ResearchSource[]>([]);
  readonly generating = input(false);
  readonly running = input(false);
  readonly buildingSheet = input(false);
  readonly saving = input(false);
  readonly approving = input(false);
  readonly editing = input(false);
  readonly retryBlocked = input(false);
  readonly dirty = input(false);
  readonly canGenerate = input(false);
  readonly canSave = input(false);
  readonly canApprove = input(false);
  readonly errorKey = input<string | null>(null);
  readonly fieldError = input<AppError | null>(null);
  readonly invalidFactIndexes = input<readonly number[]>([]);
  readonly generate = output<void>();
  readonly save = output<void>();
  readonly approve = output<void>();
  readonly edit = output<void>();
  readonly addFact = output<readonly string[]>();
  readonly patchFact = output<{ id: string; patch: FactPatch }>();
  readonly deleteFact = output<string>();
  readonly dropSource = output<{ id: string; sourceId: string }>();
  readonly approved = computed(() => this.sheet()?.status === 'approved');
  readonly busy = computed(() => this.saving() || this.approving() || this.editing());
  readonly saveReason = computed(() => this.retryBlocked() ? 'episodes.factSheet.waitForRetry' : !this.sheet() ? 'episodes.factSheet.needsSheet' : !this.dirty() ? 'episodes.factSheet.noChanges' : this.busy() ? 'episodes.factSheet.waitForWrite' : 'episodes.factSheet.invalidDraft');
  readonly approveReason = computed(() => this.retryBlocked() ? 'episodes.factSheet.waitForRetry' : !this.sheet() ? 'episodes.factSheet.needsSheet' : this.dirty() ? 'episodes.factSheet.saveFirst' : 'episodes.factSheet.waitForWrite');
  identity(fact: Fact & {readonly clientKey: string}): string { return fact.id || fact.clientKey; }
  add(): void { const source = this.sources()[0]; if (source && !this.busy()) this.addFact.emit([source.id]); }
}
