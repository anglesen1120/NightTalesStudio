import { CdkTrapFocus } from '@angular/cdk/a11y';
import {
  ChangeDetectionStrategy,
  Component,
  ElementRef,
  afterNextRender,
  computed,
  inject,
  linkedSignal,
  type Signal,
} from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatCheckboxModule } from '@angular/material/checkbox';
import { MAT_DIALOG_DATA, MatDialogRef, MatDialogTitle } from '@angular/material/dialog';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { TranslocoPipe, provideTranslocoScope } from '@jsverse/transloco';
import { LocaleService, type AppLocale } from '@core/i18n';
import type { LegalAcceptance, LegalDocumentKind, LegalDocumentStatus } from '../../domain/legal-acceptance.model';

/**
 * Open with `ariaModal: true`, `restoreFocus: true` and `disableClose: true`; the component also enforces
 * `disableClose` defensively after attachment. The future 10.2.2-T1 gate opener owns that MatDialog config.
 */
export interface LegalAcceptanceDialogData {
  readonly acceptance: Signal<LegalAcceptance | null>;
  readonly pending: Signal<boolean>;
  readonly missing: Signal<readonly LegalDocumentKind[]>;
  readonly errorKey: Signal<string | null>;
  readonly refresh: () => Promise<boolean>;
  readonly accept: (tos: string, privacy: string) => Promise<boolean>;
  readonly openedFromGate: boolean;
  /** Gate openers pass the existing `SessionStore.logout` path; non-gate openings omit the secondary action. */
  readonly signOut?: () => Promise<void>;
}

let nextLegalAcceptanceDialogId = 0;

@Component({
  selector: 'nts-legal-acceptance-dialog',
  imports: [CdkTrapFocus, MatButtonModule, MatCheckboxModule, MatDialogTitle, MatProgressSpinnerModule, TranslocoPipe],
  providers: [provideTranslocoScope('account', 'errors')],
  templateUrl: './legal-acceptance-dialog.html',
  styleUrl: './legal-acceptance-dialog.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class LegalAcceptanceDialogComponent {
  private readonly data = inject<LegalAcceptanceDialogData>(MAT_DIALOG_DATA);
  private readonly dialogRef = inject<MatDialogRef<LegalAcceptanceDialogComponent, LegalAcceptance | undefined>>(MatDialogRef, {
    optional: true,
  });
  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef);
  private readonly localeService = inject(LocaleService);

  protected readonly acceptance = this.data.acceptance;
  protected readonly pending = this.data.pending;
  protected readonly missing = this.data.missing;
  protected readonly errorKey = this.data.errorKey;
  protected readonly openedFromGate = this.data.openedFromGate;
  protected readonly canSignOut = this.data.openedFromGate && this.data.signOut !== undefined;
  private readonly currentVersions = computed<{ readonly tos: string; readonly privacy: string } | null>(
    () => {
      const acceptance = this.acceptance();
      if (acceptance === null) return null;
      const tos = acceptance.documents.find((document) => document.document === 'tos');
      const privacy = acceptance.documents.find((document) => document.document === 'privacy');
      return tos === undefined || privacy === undefined ? null : { tos: tos.currentVersion, privacy: privacy.currentVersion };
    },
    {
      equal: (previous, current) =>
        previous === current ||
        (previous !== null &&
          current !== null &&
          previous.tos === current.tos &&
          previous.privacy === current.privacy),
    },
  );
  protected readonly confirmed = linkedSignal({ source: this.currentVersions, computation: () => false });
  protected readonly titleId = `nts-legal-acceptance-title-${++nextLegalAcceptanceDialogId}`;
  protected readonly bodyId = `nts-legal-acceptance-body-${nextLegalAcceptanceDialogId}`;
  protected readonly errorId = `nts-legal-acceptance-error-${nextLegalAcceptanceDialogId}`;
  protected readonly insideMatDialog = this.dialogRef !== null;
  protected readonly privacyHref = computed(() => this.legalHref('privacy', this.localeService.locale()));
  protected readonly termsHref = computed(() => this.legalHref('tos', this.localeService.locale()));

  constructor() {
    if (this.dialogRef !== null) this.dialogRef.disableClose = true;
    afterNextRender(() => {
      this.host.nativeElement.closest('mat-dialog-container')?.setAttribute('aria-describedby', this.bodyId);
      void this.data.refresh();
    });
  }

  protected setConfirmed(checked: boolean): void {
    if (!this.pending()) this.confirmed.set(checked);
  }

  protected async submit(): Promise<void> {
    const versions = this.currentVersions();
    if (this.pending() || !this.confirmed() || versions === null) return;

    const accepted = await this.data.accept(versions.tos, versions.privacy);
    const fresh = this.acceptance();
    if (accepted && fresh !== null) this.dialogRef?.close(fresh);
  }

  protected signOut(): void {
    const signOut = this.data.signOut;
    if (!this.openedFromGate || signOut === undefined) return;
    this.dialogRef?.close();
    void signOut().catch(() => undefined);
  }

  protected onEscape(event: Event): void {
    event.preventDefault();
    event.stopPropagation();
  }

  protected labelKey(document: LegalDocumentKind): 'account.legal.termsLabel' | 'account.legal.privacyLabel' {
    return document === 'tos' ? 'account.legal.termsLabel' : 'account.legal.privacyLabel';
  }

  protected linkHref(document: LegalDocumentKind): string {
    return document === 'tos' ? this.termsHref() : this.privacyHref();
  }

  protected linkLabelKey(document: LegalDocumentKind): 'account.legal.openTerms' | 'account.legal.openPrivacy' {
    return document === 'tos' ? 'account.legal.openTerms' : 'account.legal.openPrivacy';
  }

  protected statusKey(document: LegalDocumentStatus): 'account.legal.required' | 'account.legal.outdated' | null {
    if (!this.missing().includes(document.document)) return null;
    return document.acceptedVersion === null ? 'account.legal.required' : 'account.legal.outdated';
  }

  protected acceptedOn(value: string): string {
    return this.localeService.formatDateTime(value);
  }

  private legalHref(document: LegalDocumentKind, locale: AppLocale): string {
    return `/${locale}/${document === 'tos' ? 'terms' : 'privacy'}`;
  }
}
