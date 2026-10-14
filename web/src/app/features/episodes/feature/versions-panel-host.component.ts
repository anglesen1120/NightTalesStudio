import { ChangeDetectionStrategy, Component, DestroyRef, ViewContainerRef, effect, inject, input, output, signal, untracked } from '@angular/core';
import { MatSnackBar } from '@angular/material/snack-bar';
import { TranslocoService, provideTranslocoScope } from '@jsverse/transloco';
import { firstValueFrom } from 'rxjs';
import { RealtimeService } from '@core/realtime';
import type { RealtimeSubscription } from '@core/realtime';
import { ConfirmDialog, ConflictDialog } from '@shared/ui';
import { VersionsStore } from '../application/versions.store';
import { EpisodeDetailStore } from '../application/episode-detail.store';
import { VersionsPanelComponent } from '../ui/versions-panel/versions-panel.component';

/** Feature-layer composition root: the panel itself has no store or repository dependency (D2). */
@Component({
  selector: 'nts-versions-panel-host',
  imports: [VersionsPanelComponent],
  template: `
    <nts-versions-panel
      [versions]="store.versions()" [loaded]="store.loaded()" [empty]="store.empty()"
      [leftId]="store.leftId()" [rightId]="store.rightId()" [left]="store.left()" [right]="store.right()"
      [diff]="store.diff()" [sameSelected]="store.sameSelected()" [loading]="store.loading()"
      [loadingSide]="store.loadingSide()" [sideError]="store.sideError()" [error]="store.error()"
      [canRestore]="store.canRestore()" [restoring]="store.restoring()"
      [needsReopen]="store.needsReopen()" [restoreError]="store.restoreError()"
      (selectVersion)="select($event)" (retryList)="store.retryList()" (retrySide)="store.retrySide($event)"
      (restore)="restore()" (closed)="closed.emit()" (reopenRequested)="reopenRequested.emit()"
    />
  `,
  changeDetection: ChangeDetectionStrategy.OnPush,
  providers: [provideTranslocoScope('episodes', 'errors')],
})
export class VersionsPanelHostComponent {
  readonly episodeId = input.required<string>();
  readonly initialLeft = input<string | null>(null);
  readonly initialRight = input<string | null>(null);
  readonly closed = output<void>();
  readonly reopenRequested = output<void>();
  readonly selectionChange = output<{ left: string | null; right: string | null }>();
  protected readonly store = inject(VersionsStore);
  private readonly detail = inject(EpisodeDetailStore);
  protected readonly confirming = signal(false);
  private readonly confirmDialog = inject(ConfirmDialog);
  private readonly conflictDialog = inject(ConflictDialog);
  private readonly snackbar = inject(MatSnackBar);
  private readonly transloco = inject(TranslocoService);
  private readonly viewContainerRef = inject(ViewContainerRef);
  private readonly destroyRef = inject(DestroyRef);
  private readonly realtime = inject(RealtimeService);
  private subscription: RealtimeSubscription | null = null;
  private invalidationTimer: number | undefined;

  constructor() {
    // A notice from a restore that finished with no host mounted must not resurface here (F4); an in-flight restore
    // keeps its notice for the host that remounts with it.
    this.store.discardRestoredNotice();
    let openedEpisode: string | null = null;
    effect(() => {
      const episodeId = this.episodeId();
      // Deep-link ids are tracked: a changed `?left=&right=` moves the pair without re-reading the list.
      const left = this.initialLeft();
      const right = this.initialRight();
      const ids = left === null && right === null ? undefined : { left, right };
      untracked(() => {
        if (openedEpisode !== episodeId) {
          // The mount open owns the first list read; a store the page already loaded re-reads the list (README D6).
          openedEpisode = episodeId;
          void this.store.open(episodeId, ids);
          return;
        }
        // Later changes only move the pair: the page echoes the store's own selection back through the query params,
        // which must be a no-op rather than another list read.
        if (ids !== undefined) void this.store.applySelection(ids);
      });
    });
    effect(() => {
      if (!this.store.loaded()) return;
      const ids = { left: this.store.leftId(), right: this.store.rightId() };
      untracked(() => this.selectionChange.emit(ids));
    });
    effect(() => {
      const episode = this.detail.episode();
      if (episode?.id !== this.episodeId() || this.detail.loading() || this.store.restoring() || episode.status === 'story_approved' || !this.store.needsReopen()) return;
      // This public signal is the notice's existing reset seam; preserve the loaded comparison and selection.
      untracked(() => this.store.needsReopen.set(false));
    });
    // The store belongs to the page, not this transient panel: editor reload can remount the host mid-restore.
    effect(() => {
      if (this.store.restoredVersion() === null) return;
      const episodeId = this.episodeId();
      untracked(() => { void this.announceRestored(episodeId); });
    });
    // A hub event invalidates the open list only (README D17: no polling); the subscription lives with this host.
    effect(() => {
      const episodeId = this.episodeId();
      untracked(() => this.listenForChanges(episodeId));
    });
    this.destroyRef.onDestroy(() => {
      this.stopListening();
      // No host is left to show a notice: drop it unless a restore is still in flight (F4).
      this.store.discardRestoredNotice();
    });
  }

  /** Coalesces a burst of hub events into one list re-read 250 ms after the last one (mirrors `StoryEditorStore`). */
  private listenForChanges(episodeId: string): void {
    this.stopListening();
    this.subscription = this.realtime.subscribeEpisode(episodeId, () => {
      if (this.destroyRef.destroyed) return;
      window.clearTimeout(this.invalidationTimer);
      this.invalidationTimer = window.setTimeout(() => {
        this.invalidationTimer = undefined;
        if (!this.destroyRef.destroyed) void this.store.resync();
      }, 250);
    });
  }

  private stopListening(): void {
    this.subscription?.unsubscribe();
    this.subscription = null;
    window.clearTimeout(this.invalidationTimer);
    this.invalidationTimer = undefined;
  }

  protected select(selection: { versionId: string; side: 'left' | 'right' }): void {
    if (!this.confirming() && !this.store.restoring()) void this.store.select(selection.versionId, selection.side);
  }

  protected async restore(): Promise<void> {
    const source = this.store.left();
    if (source === null || !this.store.canRestore() || this.confirming()) return;
    const episodeId = this.episodeId();
    this.confirming.set(true);
    try {
      // Scoped lookup (`selectTranslate(key, params, scope)`) keeps the runtime key `episodes.versions.version` while
      // telling keys-manager which scope owns it; resolved before the dialog so its config stays synchronous.
      const subject = await firstValueFrom(this.transloco.selectTranslate<string>('versions.version', { version: source.version }, 'episodes'));
      if (!this.isCurrent(episodeId) || this.store.left()?.versionId !== source.versionId) return;
      const confirmation = this.confirmDialog.confirm({
        titleKey: 'episodes.versions.restoreConfirm.title',
        bodyKey: this.store.editorDirty() ? 'episodes.versions.restoreConfirm.dirtyBody' : 'episodes.versions.restoreConfirm.body',
        subject,
        confirmLabelKey: 'episodes.versions.restoreConfirm.confirm',
        destructive: this.store.editorDirty(),
        pending: this.store.restoring,
      }, { viewContainerRef: this.viewContainerRef });
      const accepted = await firstValueFrom(confirmation.afterClosed());
      if (!accepted || !this.isCurrent(episodeId) || this.store.left()?.versionId !== source.versionId) return;
      const outcome = await this.store.restoreAsNewVersion();
      if (!this.isCurrent(episodeId)) return;
      if (outcome === 'conflict') await this.resolveConflict(episodeId);
      // needs_reopen / failed are rendered from store signals by the panel; no second write path.
    } finally {
      if (!this.destroyRef.destroyed) this.confirming.set(false);
    }
  }

  private async resolveConflict(episodeId: string): Promise<void> {
    while (this.isCurrent(episodeId) && this.store.pendingConflict()) {
      const dialog = this.conflictDialog.open({
        messageKey: 'episodes.versions.conflictBody',
        saveAsNewLabel: 'errors.conflict.saveAsNew',
      }, { viewContainerRef: this.viewContainerRef });
      const choice = (await firstValueFrom(dialog.afterClosed())) ?? 'cancel';
      if (!this.isCurrent(episodeId)) return;
      await this.store.resolveConflict(choice);
      if (!this.isCurrent(episodeId) || choice === 'cancel') return;
    }
  }

  private isCurrent(episodeId: string): boolean {
    return !this.destroyRef.destroyed && this.episodeId() === episodeId;
  }

  private async announceRestored(episodeId: string): Promise<void> {
    const message = await firstValueFrom(this.transloco.selectTranslate<string>('versions.restored', {}, 'episodes'));
    if (this.isCurrent(episodeId) && this.store.consumeRestored() !== null) this.snackbar.open(message, undefined, { duration: 5000 });
  }
}
