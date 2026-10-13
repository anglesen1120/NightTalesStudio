import { ChangeDetectionStrategy, Component, DestroyRef, ViewContainerRef, effect, inject, input, output, signal, untracked } from '@angular/core';
import { MatSnackBar } from '@angular/material/snack-bar';
import { TranslocoService } from '@jsverse/transloco';
import { firstValueFrom } from 'rxjs';
import { ConfirmDialog, ConflictDialog } from '@shared/ui';
import { VersionsStore } from '../application/versions.store';
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
      (restore)="restore()" (closed)="closed.emit()"
    />
  `,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class VersionsPanelHostComponent {
  readonly episodeId = input.required<string>();
  readonly initialLeft = input<string | null>(null);
  readonly initialRight = input<string | null>(null);
  readonly closed = output<void>();
  readonly selectionChange = output<{ left: string | null; right: string | null }>();
  protected readonly store = inject(VersionsStore);
  protected readonly confirming = signal(false);
  private readonly confirmDialog = inject(ConfirmDialog);
  private readonly conflictDialog = inject(ConflictDialog);
  private readonly snackbar = inject(MatSnackBar);
  private readonly transloco = inject(TranslocoService);
  private readonly viewContainerRef = inject(ViewContainerRef);
  private readonly destroyRef = inject(DestroyRef);

  constructor() {
    effect(() => {
      const episodeId = this.episodeId();
      untracked(() => {
        const left = this.initialLeft(), right = this.initialRight();
        void this.store.open(episodeId, left === null && right === null ? undefined : { left, right });
      });
    });
    effect(() => {
      if (!this.store.loaded()) return;
      const ids = { left: this.store.leftId(), right: this.store.rightId() };
      untracked(() => this.selectionChange.emit(ids));
    });
    // The store belongs to the page, not this transient panel: editor reload can remount the host mid-restore.
    effect(() => {
      if (this.store.restoredVersion() === null) return;
      const episodeId = this.episodeId();
      untracked(() => { void this.announceRestored(episodeId); });
    });
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
      const confirmation = this.confirmDialog.confirm({
        titleKey: 'episodes.versions.restoreConfirm.title',
        bodyKey: this.store.editorDirty() ? 'episodes.versions.restoreConfirm.dirtyBody' : 'episodes.versions.restoreConfirm.body',
        subject: this.transloco.translate('episodes.versions.version', { version: source.version }),
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
