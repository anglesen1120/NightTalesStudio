import { CdkTrapFocus } from '@angular/cdk/a11y';
import { NgTemplateOutlet } from '@angular/common';
import { ChangeDetectionStrategy, Component, Injectable, TemplateRef, ViewContainerRef, computed, inject, input, output } from '@angular/core';
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
  readonly pending?: boolean;
  readonly costTemplate?: TemplateRef<ConfirmDialogTemplateContext>;
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

  readonly titleKey = input.required<string>();
  readonly bodyKey = input.required<string>();
  readonly confirmLabelKey = input.required<string>();
  readonly destructive = input(false);
  readonly pending = input(false);
  readonly confirmed = output<void>();
  readonly cancelled = output<void>();

  protected readonly resolvedTitleKey = computed(() => this.data?.titleKey ?? this.titleKey());
  protected readonly resolvedBodyKey = computed(() => this.data?.bodyKey ?? this.bodyKey());
  protected readonly resolvedConfirmLabelKey = computed(() => this.data?.confirmLabelKey ?? this.confirmLabelKey());
  protected readonly isDestructive = computed(() => this.data?.destructive ?? this.destructive());
  protected readonly isPending = computed(() => this.data?.pending ?? this.pending());
  protected readonly templateContext = computed<ConfirmDialogTemplateContext>(() => ({
    destructive: this.isDestructive(),
    pending: this.isPending(),
  }));
  protected readonly costTemplate = this.data?.costTemplate;

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
}

@Injectable({ providedIn: 'root' })
export class ConfirmDialog {
  private readonly dialog = inject(MatDialog);

  confirm(data: ConfirmDialogData, options: ConfirmDialogOptions = {}): MatDialogRef<ConfirmDialogComponent, boolean> {
    return this.dialog.open<ConfirmDialogComponent, ConfirmDialogData, boolean>(ConfirmDialogComponent, {
      data: { ...data, costTemplate: options.costTemplate ?? data.costTemplate },
      viewContainerRef: options.viewContainerRef,
      autoFocus: 'first-tabbable',
      restoreFocus: true,
      closeOnNavigation: true,
      disableClose: data.pending ?? false,
      maxWidth: 'calc(100vw - 32px)',
    });
  }
}
