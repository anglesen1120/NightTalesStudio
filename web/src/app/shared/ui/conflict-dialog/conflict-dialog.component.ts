import { ChangeDetectionStrategy, Component, Injectable, ViewContainerRef, inject, output } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { MAT_DIALOG_DATA, MatDialog, MatDialogRef } from '@angular/material/dialog';
import { TranslocoPipe, provideTranslocoScope } from '@jsverse/transloco';

let nextConflictDialogId = 0;

export type ConflictDialogOutcome = 'reload' | 'saveAsNew' | 'cancel';

export interface ConflictDialogData {
  readonly messageKey: string;
  readonly detailKeys?: readonly string[];
  /**
   * Additive (10 CR-53; owner-approved extension of the frozen 10.2.2-T1 contract, 2026-10-14): pre-translated plain
   * text, interpolated only. Rendered as its own list after `detailKeys`, so callers that pass neither field render
   * exactly the DOM they rendered before.
   */
  readonly detailTexts?: readonly string[];
  /** Additive (same extension): heading translation key for `detailTexts`, rendered only when the list has entries. */
  readonly detailHeadingKey?: string;
  /** Translation key; its presence enables Save as new (normally `errors.conflict.saveAsNew`). */
  readonly saveAsNewLabel?: string;
  /** Server trace id of the conflicting response; shown as `errors.unknown.trace` in the details (text only). */
  readonly traceId?: string | null;
}

interface ResolvedConflictDialogData extends ConflictDialogData {
  readonly titleId?: string;
  readonly bodyId?: string;
}

export interface ConflictDialogOptions {
  readonly viewContainerRef?: ViewContainerRef;
}

/**
 * Cancel is first in DOM order and carries cdkFocusInitial: conflict resolution can discard unsaved work,
 * so this mirrors ConfirmDialog's destructive-action focus precedent while Material traps and restores focus.
 */
@Component({
  selector: 'nts-conflict-dialog',
  imports: [TranslocoPipe],
  providers: [provideTranslocoScope('errors')],
  templateUrl: './conflict-dialog.component.html',
  styleUrl: './conflict-dialog.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class ConflictDialogComponent {
  protected readonly data = inject<ResolvedConflictDialogData>(MAT_DIALOG_DATA);
  private readonly dialogRef = inject(MatDialogRef<ConflictDialogComponent, ConflictDialogOutcome>);
  private settled = false;

  // Outputs remain the task contract; service callers use afterClosed() because MatDialog has no template subscribers.

  readonly reload = output<void>();
  readonly saveAsNew = output<void>();
  // eslint-disable-next-line @angular-eslint/no-output-native -- 10.2.2-T1 contract output name; host is never a <dialog>.
  readonly cancel = output<void>();

  protected readonly titleId = this.data.titleId ?? `nts-conflict-title-${++nextConflictDialogId}`;
  protected readonly bodyId = this.data.bodyId ?? `nts-conflict-body-${++nextConflictDialogId}`;

  constructor() {
    // A conflict can discard unsaved work, so every dismiss path is explicit and returns `cancel`.
    this.dialogRef.disableClose = true;
    void this.dialogRef
      .keydownEvents()
      .pipe(takeUntilDestroyed())
      .forEach((event) => {
        const isEscape = event.key === 'Escape' || event.keyCode === 27;
        if (isEscape && !event.altKey && !event.ctrlKey && !event.metaKey && !event.shiftKey) {
          event.preventDefault();
          this.chooseCancel();
        }
      });
    void this.dialogRef
      .backdropClick()
      .pipe(takeUntilDestroyed())
      .forEach(() => this.chooseCancel());
  }

  protected chooseReload(): void {
    this.settle('reload');
  }

  protected chooseSaveAsNew(): void {
    this.settle('saveAsNew');
  }

  protected chooseCancel(): void {
    this.settle('cancel');
  }

  private settle(outcome: ConflictDialogOutcome): void {
    if (this.settled) return;
    this.settled = true;

    switch (outcome) {
      case 'reload':
        this.reload.emit();
        break;
      case 'saveAsNew':
        this.saveAsNew.emit();
        break;
      case 'cancel':
        this.cancel.emit();
        break;
    }
    this.dialogRef.close(outcome);
  }
}

@Injectable({ providedIn: 'root' })
export class ConflictDialog {
  private readonly dialog = inject(MatDialog);

  open(data: ConflictDialogData, options: ConflictDialogOptions = {}): MatDialogRef<ConflictDialogComponent, ConflictDialogOutcome> {
    const titleId = `nts-conflict-title-${++nextConflictDialogId}`;
    const bodyId = `nts-conflict-body-${++nextConflictDialogId}`;
    return this.dialog.open<ConflictDialogComponent, ResolvedConflictDialogData, ConflictDialogOutcome>(ConflictDialogComponent, {
      data: { ...data, titleId, bodyId },
      viewContainerRef: options.viewContainerRef,
      autoFocus: 'first-tabbable',
      restoreFocus: true,
      closeOnNavigation: true,
      disableClose: true,
      ariaModal: true,
      ariaLabelledBy: titleId,
      ariaDescribedBy: bodyId,
      maxWidth: 'calc(100vw - 32px)',
    });
  }
}
