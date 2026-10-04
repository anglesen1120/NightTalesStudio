import { CdkTrapFocus, _IdGenerator } from '@angular/cdk/a11y';
import { NgTemplateOutlet } from '@angular/common';
import { ChangeDetectionStrategy, Component, Injectable, TemplateRef, ViewContainerRef, Signal, computed, inject, input, isSignal, output } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { MAT_DIALOG_DATA, MatDialog, MatDialogRef } from '@angular/material/dialog';
import { TranslocoPipe } from '@jsverse/transloco';

export interface ConfirmDialogTemplateContext {
  readonly destructive: boolean;
  readonly pending: boolean;
}

export interface ConfirmDialogData {
  readonly titleKey: string;
  readonly bodyKey: string;
  readonly confirmLabelKey: string;
  readonly destructive?: boolean;
  /** A `Signal` lets the opener flip pending after the dialog is open; a plain boolean is fixed at open. */
  readonly pending?: boolean | Signal<boolean>;
  readonly costTemplate?: TemplateRef<ConfirmDialogTemplateContext>;
  /** Assigned by `ConfirmDialog.confirm()` so the MatDialog container can reference this instance's title/body. */
  readonly titleId?: string;
  readonly bodyId?: string;
}

export interface ConfirmDialogOptions {
  readonly costTemplate?: TemplateRef<ConfirmDialogTemplateContext>;
  readonly viewContainerRef?: ViewContainerRef;
}

@Component({
  selector: 'nts-confirm-dialog',
  imports: [CdkTrapFocus, NgTemplateOutlet, TranslocoPipe],
  templateUrl: './confirm-dialog.html',
  styleUrl: './confirm-dialog.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class ConfirmDialogComponent {
  private readonly data = inject<ConfirmDialogData | null>(MAT_DIALOG_DATA, { optional: true });
  private readonly dialogRef = inject(MatDialogRef<ConfirmDialogComponent, boolean>, { optional: true });
  private readonly ids = inject(_IdGenerator);

  readonly titleKey = input.required<string>();
  readonly bodyKey = input.required<string>();
  readonly confirmLabelKey = input.required<string>();
  readonly destructive = input(false);
  readonly pending = input(false);
  readonly confirmed = output<void>();
  readonly cancelled = output<void>();

  /** Per-instance ids: two dialogs (or a static instance next to a service dialog) never share an id. */
  protected readonly titleId = this.data?.titleId ?? this.ids.getId('nts-confirm-title-');
  protected readonly bodyId = this.data?.bodyId ?? this.ids.getId('nts-confirm-body-');
  /** True when MatDialog owns the surrounding container (its role, aria-modal and aria-labelledby). */
  protected readonly insideMatDialog = this.dialogRef !== null;

  protected readonly resolvedTitleKey = computed(() => this.data?.titleKey ?? this.titleKey());
  protected readonly resolvedBodyKey = computed(() => this.data?.bodyKey ?? this.bodyKey());
  protected readonly resolvedConfirmLabelKey = computed(() => this.data?.confirmLabelKey ?? this.confirmLabelKey());
  protected readonly isDestructive = computed(() => this.data?.destructive ?? this.destructive());
  protected readonly isPending = computed(() => {
    const fromData = this.data?.pending;
    if (fromData === undefined) return this.pending();
    return isSignal(fromData) ? fromData() : fromData;
  });
  protected readonly templateContext = computed<ConfirmDialogTemplateContext>(() => ({
    destructive: this.isDestructive(),
    pending: this.isPending(),
  }));
  protected readonly costTemplate = this.data?.costTemplate;

  constructor() {
    // The service opens the dialog with `disableClose: true` for its whole lifetime, so MatDialog never
    // closes it on its own. Escape and backdrop clicks come through cancel(), which reads the live
    // `isPending()` — pending stays authoritative even when it flips after the dialog opened.
    this.dialogRef
      ?.keydownEvents()
      .pipe(takeUntilDestroyed())
      .subscribe((event) => {
        const isEscape = event.key === 'Escape' || event.keyCode === 27;
        if (isEscape && !event.altKey && !event.ctrlKey && !event.metaKey && !event.shiftKey) {
          event.preventDefault();
          this.cancel();
        }
      });
    this.dialogRef
      ?.backdropClick()
      .pipe(takeUntilDestroyed())
      .subscribe(() => this.cancel());
  }

  protected confirm(): void {
    if (this.isPending()) return;
    this.confirmed.emit();
    this.dialogRef?.close(true);
  }

  protected cancel(): void {
    if (this.isPending()) return;
    this.cancelled.emit();
    this.dialogRef?.close(false);
  }

  /** Static (caller-owned) usage has no overlay keydown stream, so the element handles Escape itself. */
  protected onEscape(): void {
    if (!this.insideMatDialog) this.cancel();
  }
}

@Injectable({ providedIn: 'root' })
export class ConfirmDialog {
  private readonly dialog = inject(MatDialog);
  private readonly ids = inject(_IdGenerator);

  confirm(data: ConfirmDialogData, options: ConfirmDialogOptions = {}): MatDialogRef<ConfirmDialogComponent, boolean> {
    const titleId = this.ids.getId('nts-confirm-title-');
    const bodyId = this.ids.getId('nts-confirm-body-');
    return this.dialog.open<ConfirmDialogComponent, ConfirmDialogData, boolean>(ConfirmDialogComponent, {
      data: { ...data, costTemplate: options.costTemplate ?? data.costTemplate, titleId, bodyId },
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
