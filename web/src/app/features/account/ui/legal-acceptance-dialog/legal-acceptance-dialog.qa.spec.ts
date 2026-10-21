import { DOCUMENT } from '@angular/common';
import { ChangeDetectionStrategy, Component, inject, signal } from '@angular/core';
import { TestBed, type ComponentFixture } from '@angular/core/testing';
import { MatDialog, MatDialogModule, MatDialogState, type MatDialogRef } from '@angular/material/dialog';
import { NoopAnimationsModule } from '@angular/platform-browser/animations';
import { TranslocoTestingModule } from '@jsverse/transloco';
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

/** QA probes for 13.2.1-T1 (T3): consent integrity, no escape hatches, hostile strings, locale links. */

const ACCEPTANCE: LegalAcceptance = {
  documents: [
    { document: 'tos', currentVersion: '1.0.0', acceptedVersion: null, acceptedAt: null },
    { document: 'privacy', currentVersion: '1.0.0', acceptedVersion: null, acceptedAt: null },
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

class Controller {
  readonly acceptance = signal<LegalAcceptance | null>(ACCEPTANCE);
  readonly pending = signal(false);
  readonly missing = signal<readonly LegalDocumentKind[]>(['privacy', 'tos']);
  readonly errorKey = signal<string | null>(null);
  readonly acceptCalls: { readonly tos: string; readonly privacy: string }[] = [];
  signOutCalls = 0;
  acceptHandler: (tos: string, privacy: string) => Promise<boolean> = () => Promise.resolve(false);
  signOutHandler: () => Promise<void> = () => Promise.resolve();

  data(openedFromGate = true, withSignOut = true): LegalAcceptanceDialogData {
    return {
      acceptance: this.acceptance.asReadonly(),
      pending: this.pending.asReadonly(),
      missing: this.missing.asReadonly(),
      errorKey: this.errorKey.asReadonly(),
      refresh: () => Promise.resolve(true),
      accept: (tos, privacy) => {
        this.acceptCalls.push({ tos, privacy });
        return this.acceptHandler(tos, privacy);
      },
      openedFromGate,
      ...(withSignOut
        ? {
            signOut: () => {
              this.signOutCalls += 1;
              return this.signOutHandler();
            },
          }
        : {}),
    };
  }
}

@Component({
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `<button id="trigger" type="button" (click)="open()">open</button>`,
})
class Host {
  private readonly dialog = inject(MatDialog);
  data!: LegalAcceptanceDialogData;
  ref?: MatDialogRef<LegalAcceptanceDialogComponent, LegalAcceptance | undefined>;

  open(): void {
    // Deliberately the permissive config: the component must enforce disableClose itself.
    this.ref = this.dialog.open<LegalAcceptanceDialogComponent, LegalAcceptanceDialogData, LegalAcceptance | undefined>(
      LegalAcceptanceDialogComponent,
      { data: this.data, disableClose: false, restoreFocus: true, ariaModal: true },
    );
  }
}

interface Setup {
  readonly fixture: ComponentFixture<Host>;
  readonly controller: Controller;
  readonly ref: MatDialogRef<LegalAcceptanceDialogComponent, LegalAcceptance | undefined>;
  readonly dialog: HTMLElement;
  readonly document: Document;
}

async function setup(
  options: { locale?: AppLocale; openedFromGate?: boolean; withSignOut?: boolean; configure?: (c: Controller) => void } = {},
): Promise<Setup> {
  TestBed.resetTestingModule();
  await TestBed.configureTestingModule({
    imports: [Host, MatDialogModule, NoopAnimationsModule, translations],
    providers: ZONELESS_TEST_PROVIDERS,
  }).compileComponents();
  TestBed.inject(LocaleService).activate(options.locale ?? 'en', { persist: false });
  const controller = new Controller();
  options.configure?.(controller);
  const fixture = TestBed.createComponent(Host);
  fixture.componentInstance.data = controller.data(options.openedFromGate ?? true, options.withSignOut ?? true);
  fixture.detectChanges();
  vi.spyOn(HTMLElement.prototype, 'getClientRects').mockReturnValue([{}] as unknown as DOMRectList);
  fixture.componentInstance.open();
  fixture.detectChanges();
  await fixture.whenStable();
  const document = TestBed.inject(DOCUMENT);
  const dialog = document.querySelector<HTMLElement>('nts-legal-acceptance-dialog');
  const ref = fixture.componentInstance.ref;
  if (dialog === null || ref === undefined) throw new Error('legal acceptance dialog did not open');
  return { fixture, controller, ref, dialog, document };
}

async function tick(fixture: ComponentFixture<Host>): Promise<void> {
  fixture.detectChanges();
  await fixture.whenStable();
}

function checkbox(dialog: HTMLElement): HTMLInputElement {
  const input = dialog.querySelector<HTMLInputElement>('mat-checkbox input[type="checkbox"]');
  if (input === null) throw new Error('checkbox not rendered');
  return input;
}

function acceptButton(dialog: HTMLElement): HTMLButtonElement {
  const button = dialog.querySelector<HTMLButtonElement>('.legal-dialog__accept');
  if (button === null) throw new Error('accept button not rendered');
  return button;
}

afterEach(() => {
  vi.restoreAllMocks();
  TestBed.resetTestingModule();
});

describe('LegalAcceptanceDialogComponent QA', () => {
  it('requires a fresh confirmation when a 409 refresh shows a different version than the one ticked', async () => {
    const { fixture, controller, dialog } = await setup({
      configure: (value) => {
        value.acceptHandler = () => {
          // What the store does on legal_version_outdated: error + refreshed projection with a NEW version.
          value.errorKey.set('errors.legal_version_outdated');
          value.acceptance.set({
            documents: [
              { document: 'tos', currentVersion: '1.1.0', acceptedVersion: null, acceptedAt: null },
              { document: 'privacy', currentVersion: '1.0.0', acceptedVersion: null, acceptedAt: null },
            ],
          });
          return Promise.resolve(false);
        };
      },
    });
    checkbox(dialog).click();
    await tick(fixture);
    acceptButton(dialog).click();
    await tick(fixture);

    expect(dialog.textContent).toContain('1.1.0');
    // Consent is of what was shown: the tick was for 1.0.0, so the new version must not be one click away.
    expect(checkbox(dialog).checked).toBe(false);
    expect(acceptButton(dialog).disabled).toBe(true);
    expect(controller.acceptCalls).toEqual([{ tos: '1.0.0', privacy: '1.0.0' }]);
  });

  it('cannot be dismissed by Escape from inside the dialog, a backdrop click or a programmatic close request', async () => {
    const { fixture, ref, dialog, document } = await setup();
    const escape = new KeyboardEvent('keydown', { key: 'Escape', keyCode: 27, bubbles: true, cancelable: true });
    dialog.querySelector<HTMLElement>('.legal-dialog__title')?.dispatchEvent(escape);
    document.querySelector<HTMLElement>('.cdk-overlay-backdrop')?.click();
    await tick(fixture);

    expect(escape.defaultPrevented).toBe(true);
    expect(ref.disableClose).toBe(true);
    expect(ref.getState()).toBe(MatDialogState.OPEN);
  });

  it('sends the rendered versions only and never twice while a request is pending', async () => {
    const { fixture, controller, dialog } = await setup();
    checkbox(dialog).click();
    await tick(fixture);
    const accept = acceptButton(dialog);
    accept.click();
    controller.pending.set(true);
    await tick(fixture);
    expect(accept.disabled).toBe(true);
    expect(checkbox(dialog).disabled).toBe(true);
    accept.click();
    await tick(fixture);

    expect(controller.acceptCalls).toEqual([{ tos: '1.0.0', privacy: '1.0.0' }]);
  });

  it('keeps the accept action disabled until a projection exists, even if a confirmation was somehow set', async () => {
    const { dialog } = await setup({ configure: (value) => value.acceptance.set(null) });

    expect(dialog.querySelector('mat-checkbox')).toBeNull();
    expect(acceptButton(dialog).disabled).toBe(true);
  });

  it('renders hostile version text as text, never as markup', async () => {
    const hostile = '<img src=x onerror="document.title=\'pwned\'">';
    const { dialog } = await setup({
      configure: (value) =>
        value.acceptance.set({
          documents: [
            { document: 'tos', currentVersion: hostile, acceptedVersion: null, acceptedAt: null },
            { document: 'privacy', currentVersion: '1.0.0', acceptedVersion: null, acceptedAt: null },
          ],
        }),
    });

    expect(dialog.querySelector('img')).toBeNull();
    expect(dialog.textContent).toContain(hostile);
  });

  it('follows the active locale for both links without reopening', async () => {
    const { fixture, dialog } = await setup({ locale: 'vi' });
    const hrefs = () => [...dialog.querySelectorAll('a')].map((link) => link.getAttribute('href'));
    expect(hrefs()).toEqual(['/vi/terms', '/vi/privacy']);

    TestBed.inject(LocaleService).activate('en', { persist: false });
    await tick(fixture);

    expect(hrefs()).toEqual(['/en/terms', '/en/privacy']);
  });

  it('shows sign out only for a gate opening that also supplies the sign-out action', async () => {
    const withHandler = await setup({ openedFromGate: true, withSignOut: true });
    const label = accountEn.legal.signOut;
    const has = (root: HTMLElement) => [...root.querySelectorAll('button')].some((b) => b.textContent?.includes(label));
    expect(has(withHandler.dialog)).toBe(true);

    const withoutHandler = await setup({ openedFromGate: true, withSignOut: false });
    expect(has(withoutHandler.dialog)).toBe(false);

    const notGate = await setup({ openedFromGate: false, withSignOut: true });
    expect(has(notGate.dialog)).toBe(false);
  });

  it('keeps sign out enabled on a failed accept and survives a rejecting sign-out action', async () => {
    const { fixture, controller, dialog } = await setup({
      configure: (value) => {
        value.errorKey.set('account.legal.error.generic');
        value.signOutHandler = () => Promise.reject(new Error('network down'));
      },
    });
    const signOut = [...dialog.querySelectorAll('button')].find((b) => b.textContent?.includes(accountEn.legal.signOut));
    expect(signOut?.disabled).toBe(false);

    signOut?.click();
    await tick(fixture);
    await Promise.resolve();

    expect(controller.signOutCalls).toBe(1);
  });
});
