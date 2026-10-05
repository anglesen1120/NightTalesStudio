import { DOCUMENT } from '@angular/common';
import { ChangeDetectionStrategy, Component, inject, signal } from '@angular/core';
import { TestBed, type ComponentFixture } from '@angular/core/testing';
import { MatDialog, MatDialogModule, MatDialogState, type MatDialogRef } from '@angular/material/dialog';
import { NoopAnimationsModule } from '@angular/platform-browser/animations';
import { TranslocoTestingModule } from '@jsverse/transloco';
import { firstValueFrom } from 'rxjs';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { LocaleService, type AppLocale } from '@core/i18n';
import { ZONELESS_TEST_PROVIDERS } from '../../../../testing/providers';
import accountEn from '../../../../../assets/i18n/account/en.json';
import accountVi from '../../../../../assets/i18n/account/vi.json';
import coreEn from '../../../../../assets/i18n/core/en.json';
import coreVi from '../../../../../assets/i18n/core/vi.json';
import errorsEn from '../../../../../assets/i18n/errors/en.json';
import errorsVi from '../../../../../assets/i18n/errors/vi.json';
import type { LegalAcceptance, LegalDocumentKind } from '../../domain/legal-acceptance.model';
import { LegalAcceptanceDialogComponent, type LegalAcceptanceDialogData } from './legal-acceptance-dialog';

const ACCEPTANCE: LegalAcceptance = {
  documents: [
    { document: 'tos', currentVersion: '1.0.0', acceptedVersion: '0.9.0', acceptedAt: '2026-10-01T08:00:00Z' },
    { document: 'privacy', currentVersion: '1.0.0', acceptedVersion: null, acceptedAt: null },
  ],
};

const FRESH_ACCEPTANCE: LegalAcceptance = {
  documents: [
    { document: 'tos', currentVersion: '1.0.0', acceptedVersion: '1.0.0', acceptedAt: '2026-10-05T08:00:00Z' },
    { document: 'privacy', currentVersion: '1.0.0', acceptedVersion: '1.0.0', acceptedAt: '2026-10-05T08:00:00Z' },
  ],
};

const translations = TranslocoTestingModule.forRoot({
  langs: {
    en: coreEn,
    vi: coreVi,
    'account/en': accountEn,
    'account/vi': accountVi,
    'errors/en': errorsEn,
    'errors/vi': errorsVi,
  },
  translocoConfig: { availableLangs: ['vi', 'en'], defaultLang: 'vi', fallbackLang: 'vi' },
});

interface Deferred<T> {
  readonly promise: Promise<T>;
  readonly resolve: (value: T) => void;
}

function deferred<T>(): Deferred<T> {
  let resolve!: (value: T) => void;
  const promise = new Promise<T>((resolvePromise) => {
    resolve = resolvePromise;
  });
  return { promise, resolve };
}

class DialogController {
  readonly acceptance = signal<LegalAcceptance | null>(ACCEPTANCE);
  readonly pending = signal(false);
  readonly missing = signal<readonly LegalDocumentKind[]>(['privacy', 'tos']);
  readonly errorKey = signal<string | null>(null);
  readonly acceptCalls: { readonly tos: string; readonly privacy: string }[] = [];
  refreshCalls = 0;
  signOutCalls = 0;
  acceptHandler: (tos: string, privacy: string) => Promise<boolean> = () => Promise.resolve(false);

  refresh(): Promise<boolean> {
    this.refreshCalls += 1;
    return Promise.resolve(true);
  }

  accept(tos: string, privacy: string): Promise<boolean> {
    this.acceptCalls.push({ tos, privacy });
    return this.acceptHandler(tos, privacy);
  }

  signOut(): Promise<void> {
    this.signOutCalls += 1;
    return Promise.resolve();
  }

  data(openedFromGate = true): LegalAcceptanceDialogData {
    return {
      acceptance: this.acceptance.asReadonly(),
      pending: this.pending.asReadonly(),
      missing: this.missing.asReadonly(),
      errorKey: this.errorKey.asReadonly(),
      refresh: () => this.refresh(),
      accept: (tos, privacy) => this.accept(tos, privacy),
      openedFromGate,
      signOut: () => this.signOut(),
    };
  }
}

@Component({
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `<button id="trigger" type="button" (click)="open()">open</button>`,
})
class DialogHost {
  private readonly dialog = inject(MatDialog);
  data!: LegalAcceptanceDialogData;
  ref?: MatDialogRef<LegalAcceptanceDialogComponent, LegalAcceptance | undefined>;

  open(): void {
    this.ref = this.dialog.open<LegalAcceptanceDialogComponent, LegalAcceptanceDialogData, LegalAcceptance | undefined>(
      LegalAcceptanceDialogComponent,
      { data: this.data, disableClose: false, restoreFocus: true, ariaModal: true },
    );
  }
}

interface DialogSetup {
  readonly fixture: ComponentFixture<DialogHost>;
  readonly controller: DialogController;
  readonly ref: MatDialogRef<LegalAcceptanceDialogComponent, LegalAcceptance | undefined>;
  readonly dialog: HTMLElement;
  readonly document: Document;
}

async function setup(
  locale: AppLocale = 'vi',
  configure?: (controller: DialogController) => void,
  openedFromGate = true,
): Promise<DialogSetup> {
  TestBed.resetTestingModule();
  await TestBed.configureTestingModule({
    imports: [DialogHost, MatDialogModule, NoopAnimationsModule, translations],
    providers: ZONELESS_TEST_PROVIDERS,
  }).compileComponents();
  TestBed.inject(LocaleService).activate(locale, { persist: false });
  const controller = new DialogController();
  configure?.(controller);
  const fixture = TestBed.createComponent(DialogHost);
  fixture.componentInstance.data = controller.data(openedFromGate);
  fixture.detectChanges();
  vi.spyOn(HTMLElement.prototype, 'getClientRects').mockReturnValue([{}] as unknown as DOMRectList);
  (fixture.nativeElement as HTMLElement).querySelector<HTMLButtonElement>('#trigger')?.focus();
  fixture.componentInstance.open();
  fixture.detectChanges();
  await fixture.whenStable();
  const document = TestBed.inject(DOCUMENT);
  const dialog = document.querySelector<HTMLElement>('nts-legal-acceptance-dialog');
  const ref = fixture.componentInstance.ref;
  if (dialog === null || ref === undefined) throw new Error('legal acceptance dialog did not open');
  return { fixture, controller, ref, dialog, document };
}

async function checkCheckbox(dialog: HTMLElement, fixture: ComponentFixture<DialogHost>): Promise<void> {
  const checkbox = dialog.querySelector<HTMLInputElement>('mat-checkbox input[type="checkbox"]');
  if (checkbox === null) throw new Error('checkbox not rendered');
  checkbox.click();
  fixture.detectChanges();
  await fixture.whenStable();
}

afterEach(() => {
  vi.restoreAllMocks();
  TestBed.resetTestingModule();
});

describe('LegalAcceptanceDialogComponent', () => {
  it.each([
    ['vi', accountVi.legal.title, accountVi.legal.termsLabel, accountVi.legal.privacyLabel],
    ['en', accountEn.legal.title, accountEn.legal.termsLabel, accountEn.legal.privacyLabel],
  ] as const)('renders both documents, versions and %s copy', async (locale, title, terms, privacy) => {
    const { dialog, controller } = await setup(locale);

    expect(dialog.querySelector('h2')?.textContent).toContain(title);
    expect(dialog.textContent).toContain(terms);
    expect(dialog.textContent).toContain(privacy);
    expect(dialog.textContent?.match(/1\.0\.0/g)?.length).toBe(2);
    const links = [...dialog.querySelectorAll<HTMLAnchorElement>('a')];
    expect(links.map((link) => link.getAttribute('href'))).toEqual([`/${locale}/terms`, `/${locale}/privacy`]);
    expect(links.every((link) => link.target === '_blank' && link.rel === 'noopener noreferrer')).toBe(true);
    expect(links.every((link) => (link.getAttribute('aria-label') ?? '').length > 0)).toBe(true);
    expect(controller.refreshCalls).toBe(1);
  });

  it('focuses the labelled title and traps focus in an aria-modal dialog', async () => {
    const { dialog, document } = await setup();
    const title = dialog.querySelector<HTMLElement>('.legal-dialog__title');
    const container = document.querySelector('mat-dialog-container');

    await vi.waitFor(() => expect(document.activeElement).toBe(title));
    await vi.waitFor(() => expect(container?.getAttribute('aria-labelledby')).toBe(title?.id));
    expect(container?.getAttribute('aria-modal')).toBe('true');
  });

  it('gates the primary action and closes only after 200 with the fresh projection', async () => {
    const response = deferred<boolean>();
    const { fixture, controller, ref, dialog } = await setup('en', (value) => {
      value.acceptHandler = () => response.promise;
    });
    const accept = dialog.querySelector<HTMLButtonElement>('.legal-dialog__accept')!;
    expect(accept.disabled).toBe(true);

    await checkCheckbox(dialog, fixture);
    expect(accept.disabled).toBe(false);
    const closed = firstValueFrom(ref.afterClosed());
    accept.click();
    await fixture.whenStable();
    expect(controller.acceptCalls).toEqual([{ tos: '1.0.0', privacy: '1.0.0' }]);
    expect(ref.getState()).toBe(MatDialogState.OPEN);

    controller.acceptance.set(FRESH_ACCEPTANCE);
    response.resolve(true);
    await expect(closed).resolves.toEqual(FRESH_ACCEPTANCE);
  });

  it('keeps the dialog open and shows errors.legal_version_outdated after a 409', async () => {
    const { fixture, controller, ref, dialog } = await setup('en', (value) => {
      value.acceptHandler = () => {
        value.errorKey.set('errors.legal_version_outdated');
        return Promise.resolve(false);
      };
    });
    await checkCheckbox(dialog, fixture);
    dialog.querySelector<HTMLButtonElement>('.legal-dialog__accept')?.click();
    await fixture.whenStable();

    expect(ref.getState()).toBe(MatDialogState.OPEN);
    expect(dialog.querySelector('[role="alert"]')?.textContent).toContain(errorsEn.legal_version_outdated);
    expect(dialog.querySelector<HTMLInputElement>('mat-checkbox input')?.checked).toBe(true);
    expect(controller.acceptCalls).toHaveLength(1);
  });

  it('keeps the checkbox state and shows generic copy after a 500', async () => {
    const { fixture, ref, dialog } = await setup('en', (value) => {
      value.acceptHandler = () => {
        value.errorKey.set('account.legal.error.generic');
        return Promise.resolve(false);
      };
    });
    await checkCheckbox(dialog, fixture);
    dialog.querySelector<HTMLButtonElement>('.legal-dialog__accept')?.click();
    await fixture.whenStable();

    expect(ref.getState()).toBe(MatDialogState.OPEN);
    expect(dialog.querySelector('[role="alert"]')?.textContent).toContain(accountEn.legal.error.generic);
    expect(dialog.querySelector<HTMLInputElement>('mat-checkbox input')?.checked).toBe(true);
  });

  it('does not close on Escape or an outside click', async () => {
    const { fixture, ref, document } = await setup();
    document.body.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', keyCode: 27, bubbles: true }));
    document.querySelector<HTMLElement>('.cdk-overlay-backdrop')?.click();
    await fixture.whenStable();

    expect(ref.disableClose).toBe(true);
    expect(ref.getState()).toBe(MatDialogState.OPEN);
  });

  it('shows a labelled loading state while the initial refresh is pending', async () => {
    const { dialog } = await setup('en', (controller) => {
      controller.acceptance.set(null);
      controller.pending.set(true);
    });

    expect(dialog.querySelector('[role="status"]')?.textContent).toContain(coreEn.common.loading);
    expect(dialog.querySelector<HTMLButtonElement>('.legal-dialog__accept')?.disabled).toBe(true);
  });

  it('offers sign out only for a gate opening and never disables it while acceptance is pending', async () => {
    const { fixture, controller, dialog } = await setup('en', (value) => value.pending.set(true));
    const signOut = [...dialog.querySelectorAll<HTMLButtonElement>('button')].find((button) =>
      button.textContent?.includes(accountEn.legal.signOut),
    );
    expect(signOut?.disabled).toBe(false);
    signOut?.click();
    await fixture.whenStable();
    expect(controller.signOutCalls).toBe(1);
  });

  it('omits sign out when the dialog was not opened from the gate', async () => {
    const { dialog } = await setup('en', undefined, false);
    const buttons = [...dialog.querySelectorAll<HTMLButtonElement>('button')];
    expect(buttons.some((button) => button.textContent?.includes(accountEn.legal.signOut))).toBe(false);
  });
});
