import { ChangeDetectionStrategy, Component, DestroyRef, ViewContainerRef, computed, effect, inject, signal, untracked, viewChild } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { TranslocoPipe, provideTranslocoScope } from '@jsverse/transloco';
import { firstValueFrom } from 'rxjs';
import { AppErrorMapper, type Result } from '@core/errors';
import { UnsavedWorkRegistry } from '@core/shell/unsaved-work';
import { ConfirmDialog, ConflictDialog, EmptyState, ErrorState, LoadingState, PageHeader, RetryCountdown } from '@shared/ui';
import { ResearchStore } from '../../application/research.store';
import { SourceFinderStore } from '../../application/source-finder.store';
import { EPISODE_GATE } from '../../application/episode-gate';
import type { ResearchSource } from '../../domain/research.model';
import { SourceAddFormComponent } from '../../ui/source-add-form/source-add-form.component';
import { SourceRowComponent } from '../../ui/source-row/source-row.component';
import { FactSheetPanelComponent } from '../../ui/fact-sheet-panel/fact-sheet-panel.component';
import { SourceFinderPanelComponent } from '../../ui/source-finder-panel/source-finder-panel.component';
@Component({ selector: 'nts-research-page', imports: [TranslocoPipe, RouterLink, PageHeader, LoadingState, ErrorState, EmptyState, RetryCountdown, SourceAddFormComponent, SourceRowComponent, FactSheetPanelComponent, SourceFinderPanelComponent], providers: [ResearchStore, SourceFinderStore, { provide: EPISODE_GATE, useFactory: () => inject(ResearchStore).gate }, provideTranslocoScope('episodes', 'errors')], templateUrl: './research-page.component.html', styleUrl: './research-page.component.scss', changeDetection: ChangeDetectionStrategy.OnPush, host: { '(window:beforeunload)': 'beforeUnload($event)' } })
export class ResearchPageComponent {
  readonly store = inject(ResearchStore);
  readonly finder = inject(SourceFinderStore);
  private readonly route = inject(ActivatedRoute);
  private readonly params = toSignal(this.route.paramMap, { initialValue: this.route.snapshot.paramMap });
  readonly episodeId = computed(() => this.params().get('id') ?? this.route.parent?.snapshot.paramMap.get('id') ?? '');
  private readonly destroy = inject(DestroyRef);
  private readonly mapper = inject(AppErrorMapper);
  private readonly confirmDialog = inject(ConfirmDialog);
  private readonly conflictDialog = inject(ConflictDialog);
  private readonly viewContainerRef = inject(ViewContainerRef);
  private readonly unsaved = inject(UnsavedWorkRegistry);
  private readonly form = viewChild(SourceAddFormComponent);
  readonly adding = signal(false);
  readonly rowPending = signal<ReadonlySet<string>>(new Set());
  readonly editing = signal(false);
  readonly retryBlocked = computed(() => this.store.rateLimitSeconds() !== null);
  readonly canAddUrl = computed(() => { const limits = this.store.limits(); return this.store.canAddUrl() && (limits === null || limits.urlSourcesLast24h < limits.maxUrlSourcesPerDay); });
  readonly loadErrorKey = computed(() => { const error = this.store.error(); return error ? this.mapper.messageKey(error) : 'errors.unknown_error'; });
  readonly formErrorKey = computed(() => { const error = this.store.formError(); return error ? this.mapper.messageKey(error) : null; });
  readonly panelErrorKey = computed(() => { const error = this.store.panelError(); return error ? this.mapper.messageKey(error) : null; });
  readonly finderErrorKey = computed(() => { const error = this.finder.error(); return error ? this.mapper.messageKey(error) : null; });
  readonly finderRowErrorKeys = computed(() => new Map([...this.finder.rowErrors()].map(([id, error]) => [id, this.mapper.messageKey(error)])));
  readonly invalidFactIndexes = computed(() => {
    const error = this.store.panelError();
    if (error?.code !== 'fact_sheet_invalid_content') return [];
    const indexes = new Set<number>();
    for (const path of Object.keys(error.fieldErrors)) {
      const match = /(?:content\.)?facts(?:\.|\[)(\d+)/i.exec(path);
      if (match?.[1]) indexes.add(Number(match[1]));
    }
    return [...indexes];
  });
  readonly duplicateSource = computed(() => {
    if (this.store.formError()?.code !== 'source_duplicate') return null;
    const url = this.form()?.value().trim();
    return this.store.sources().find(source => source.url === url || source.finalUrl === url)?.id ?? null;
  });
  private shownConflict: object | null = null;
  private leavePrompt: Promise<boolean> | null = null;
  constructor() {
    effect(() => { const id = this.episodeId(); if (id) untracked(() => { void this.store.load(id); this.store.watch(id); this.finder.watch(id); }); });
    effect(() => {
      const conflict = this.store.conflict();
      if (!conflict || conflict === this.shownConflict || this.store.savingFactSheet() || this.store.approvingFactSheet()) return;
      this.shownConflict = conflict;
      untracked(() => { void this.resolveConflict(); });
    });
    this.destroy.onDestroy(this.unsaved.register({ hasUnsaved: () => this.hasUnsaved(), discard: () => this.store.reset(), confirmLeave: () => this.confirmLeave() }));
  }
  private hasUnsaved(): boolean {
    if (this.store.dirty() || this.store.savingFactSheet()) return true;
    const saved = this.store.factSheet();
    if (!saved || saved.status === 'approved') return false;
    const shape = (facts: typeof saved.facts): string => JSON.stringify(facts.map(({id, statement, confidence, sourceIds}) => ({id, statement, confidence, sourceIds})));
    return shape(this.store.draft()) !== shape(saved.facts);
  }
  beforeUnload(event: BeforeUnloadEvent): void { if (this.hasUnsaved()) { event.preventDefault(); event.returnValue = ''; } }
  confirmLeave(): Promise<boolean> {
    if (!this.hasUnsaved()) return Promise.resolve(true);
    this.leavePrompt ??= this.askLeave().finally(() => { this.leavePrompt = null; });
    return this.leavePrompt;
  }
  private async askLeave(): Promise<boolean> {
    const ref = this.confirmDialog.confirm({ titleKey: 'episodes.dirty.title', bodyKey: 'episodes.dirty.body', confirmLabelKey: 'episodes.dirty.leave', cancelLabelKey: 'episodes.dirty.stay', destructive: true }, { viewContainerRef: this.viewContainerRef });
    const leave = (await firstValueFrom(ref.afterClosed())) ?? false;
    if (leave && !this.destroy.destroyed) this.store.reset();
    return leave;
  }
  async add(kind: 'url' | 'note', value: string): Promise<void> {
    if (this.adding() || this.retryBlocked()) return;
    const id = this.episodeId();
    this.adding.set(true);
    try {
      const result = kind === 'url' ? await this.store.addUrl(id, value) : await this.store.addNote(id, value);
      if (result.ok && !this.destroy.destroyed && id === this.episodeId()) this.form()?.clear();
    }
    finally { if (!this.destroy.destroyed) this.adding.set(false); }
  }
  async sourceAction(source: ResearchSource, intent: 'remove' | 'retry'): Promise<void> {
    if (this.rowPending().has(source.id) || this.retryBlocked()) return;
    const id = this.episodeId();
    this.rowPending.update(current => new Set([...current, source.id]));
    try {
      if (intent === 'remove' && source.isCited) {
        const ref = this.confirmDialog.confirm({ titleKey: 'episodes.research.sources.remove', bodyKey: 'episodes.research.sources.deleteCitedConfirm', confirmLabelKey: 'episodes.research.sources.remove', destructive: true }, { viewContainerRef: this.viewContainerRef });
        if (!(await firstValueFrom(ref.afterClosed())) || this.destroy.destroyed || id !== this.episodeId()) return;
      }
      if (intent === 'remove') await this.store.remove(id, source.id); else await this.store.retry(id, source.id);
    } finally { if (!this.destroy.destroyed) this.rowPending.update(current => { const next = new Set(current); next.delete(source.id); return next; }); }
  }
  async startEdit(): Promise<void> { if (this.editing() || this.retryBlocked()) return; this.editing.set(true); try { await this.store.startEdit(this.episodeId()); } finally { if (!this.destroy.destroyed) this.editing.set(false); } }
  private async resolveConflict(): Promise<void> {
    const id = this.episodeId();
    const source = this.store.conflictSource();
    const ref = this.conflictDialog.open({ messageKey: 'errors.fact_sheet_conflict', ...(source === 'save' ? { saveAsNewLabel: 'errors.conflict.saveAsNew' } : {}) }, { viewContainerRef: this.viewContainerRef });
    const outcome = await firstValueFrom(ref.afterClosed());
    if (this.destroy.destroyed || id !== this.episodeId()) return;
    if (outcome === 'reload') await this.store.reloadLatest(id);
    if (outcome === 'saveAsNew' && source === 'save') await this.store.saveMineAsNewVersion(id);
  }
  run(action: 'generate' | 'save' | 'approve' | 'refresh'): void { if (action !== 'refresh' && this.retryBlocked()) return; const promise: Promise<Result<void>> = this.store[action](this.episodeId()); void promise; }
  clearFinderRowRetry(): void {
    for (const [id, error] of this.finder.rowErrors()) if (error.code === 'source_fetch_limit_reached') this.finder.clearRowError(id);
  }
}
