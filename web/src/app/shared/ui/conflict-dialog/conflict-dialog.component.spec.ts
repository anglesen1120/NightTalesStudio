import { ChangeDetectionStrategy, Component, inject } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { MatDialogModule, type MatDialogRef } from '@angular/material/dialog';
import { NoopAnimationsModule } from '@angular/platform-browser/animations';
import { TranslocoTestingModule } from '@jsverse/transloco';
import { describe, expect, it, vi } from 'vitest';
import errorsEn from '../../../../assets/i18n/errors/en.json';
import { onEmit } from '../testing/on-emit';
import { ConflictDialog, ConflictDialogComponent, type ConflictDialogOutcome } from './conflict-dialog.component';

const translations = TranslocoTestingModule.forRoot({
  langs: {
    en: {},
    'errors/en': errorsEn,
  },
  translocoConfig: { availableLangs: ['en'], defaultLang: 'en', fallbackLang: 'en' },
  preloadLangs: true,
});

@Component({
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `<button id="trigger" type="button" (click)="open()">open</button>`,
})
class DialogHost {
  private readonly dialogs = inject(ConflictDialog);
  ref?: MatDialogRef<ConflictDialogComponent, ConflictDialogOutcome>;

  open(): void {
    this.ref = this.dialogs.open({
      messageKey: 'errors.concurrency_conflict',
      saveAsNewLabel: 'errors.conflict.saveAsNew',
    });
  }
}

describe('ConflictDialog', () => {
  it('ConflictDialog_EmitsChosenOutcome', async () => {
    await TestBed.configureTestingModule({
      imports: [DialogHost, MatDialogModule, NoopAnimationsModule, translations],
    }).compileComponents();
    const fixture = TestBed.createComponent(DialogHost);
    fixture.detectChanges();
    const dialogs = TestBed.inject(ConflictDialog);
    const data = {
      messageKey: 'errors.concurrency_conflict',
      detailKeys: ['errors.validation_failed'],
      saveAsNewLabel: 'errors.conflict.saveAsNew',
    };

    for (const outcome of ['reload', 'saveAsNew', 'cancel'] as const) {
      const ref = dialogs.open(data);
      const closed = vi.fn();
      const emitted = vi.fn();
      onEmit(ref.afterClosed(), closed);
      onEmit(ref.componentInstance[outcome], emitted);
      await fixture.whenStable();
      expect(document.querySelector('.cdk-overlay-pane .conflict-dialog__body')?.textContent).toContain(errorsEn.validation_failed);
      const button = document.querySelector<HTMLButtonElement>(`.cdk-overlay-pane button[data-outcome="${outcome}"]`);
      if (button === null) throw new Error(`missing ${outcome} button`);
      button.click();
      await fixture.whenStable();
      expect(emitted).toHaveBeenCalledTimes(1);
      expect(closed).toHaveBeenCalledExactlyOnceWith(outcome);
    }

    const trigger = (fixture.nativeElement as HTMLElement).querySelector<HTMLButtonElement>('#trigger');
    if (trigger === null) throw new Error('missing trigger');
    const geometry = vi.spyOn(HTMLElement.prototype, 'getClientRects').mockReturnValue([{}] as unknown as DOMRectList);
    try {
      trigger.focus();
      trigger.click();
      await fixture.whenStable();
      const ref = fixture.componentInstance.ref;
      if (ref === undefined) throw new Error('dialog did not open');
      const cancelled = vi.fn();
      const closed = vi.fn();
      onEmit(ref.componentInstance.cancel, cancelled);
      onEmit(ref.afterClosed(), closed);
      const cancelButton = document.querySelector<HTMLButtonElement>('.cdk-overlay-pane button[data-outcome="cancel"]');
      if (cancelButton === null) throw new Error('missing cancel button');
      await vi.waitFor(() => expect(document.activeElement).toBe(cancelButton));

      document.body.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', keyCode: 27, bubbles: true }));
      await fixture.whenStable();
      expect(cancelled).toHaveBeenCalledTimes(1);
      expect(closed).toHaveBeenCalledExactlyOnceWith('cancel');
      expect(document.activeElement).toBe(trigger);
    } finally {
      geometry.mockRestore();
    }
  });

  it('omits Save as new unless its translation-key label is provided', async () => {
    await TestBed.configureTestingModule({
      imports: [DialogHost, MatDialogModule, NoopAnimationsModule, translations],
    }).compileComponents();
    const fixture = TestBed.createComponent(DialogHost);
    fixture.detectChanges();
    const ref = TestBed.inject(ConflictDialog).open({ messageKey: 'errors.concurrency_conflict' });
    await fixture.whenStable();
    const labels = [...document.querySelectorAll<HTMLButtonElement>('.cdk-overlay-pane .conflict-dialog__actions button')]
      .map((button) => button.textContent?.trim());
    expect(labels).toEqual([errorsEn.conflict.cancel, errorsEn.conflict.reload]);
    ref.close('cancel');
    await fixture.whenStable();
  });

  it('maps a backdrop dismissal to cancel', async () => {
    await TestBed.configureTestingModule({
      imports: [DialogHost, MatDialogModule, NoopAnimationsModule, translations],
    }).compileComponents();
    const fixture = TestBed.createComponent(DialogHost);
    fixture.detectChanges();
    const ref = TestBed.inject(ConflictDialog).open({ messageKey: 'errors.concurrency_conflict' });
    const cancelled = vi.fn();
    const closed = vi.fn();
    onEmit(ref.componentInstance.cancel, cancelled);
    onEmit(ref.afterClosed(), closed);
    await fixture.whenStable();
    const backdrop = document.querySelector<HTMLElement>('.cdk-overlay-backdrop');
    if (backdrop === null) throw new Error('missing dialog backdrop');
    backdrop.click();
    await fixture.whenStable();
    expect(cancelled).toHaveBeenCalledTimes(1);
    expect(closed).toHaveBeenCalledExactlyOnceWith('cancel');
  });

  it('settles as reload when Escape follows the Reload click before closing completes', async () => {
    await TestBed.configureTestingModule({
      imports: [DialogHost, MatDialogModule, NoopAnimationsModule, translations],
    }).compileComponents();
    const fixture = TestBed.createComponent(DialogHost);
    fixture.detectChanges();
    const ref = TestBed.inject(ConflictDialog).open({ messageKey: 'errors.concurrency_conflict' });
    const reloaded = vi.fn();
    const cancelled = vi.fn();
    const closed = vi.fn();
    onEmit(ref.componentInstance.reload, reloaded);
    onEmit(ref.componentInstance.cancel, cancelled);
    onEmit(ref.afterClosed(), closed);
    await fixture.whenStable();
    const reloadButton = document.querySelector<HTMLButtonElement>('.cdk-overlay-pane button[data-outcome="reload"]');
    if (reloadButton === null) throw new Error('missing reload button');

    reloadButton.click();
    document.body.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', keyCode: 27, bubbles: true }));
    await fixture.whenStable();

    expect(reloaded).toHaveBeenCalledTimes(1);
    expect(cancelled).not.toHaveBeenCalled();
    expect(closed).toHaveBeenCalledExactlyOnceWith('reload');
  });

  it('emits Save as new only once when its button is double-clicked', async () => {
    await TestBed.configureTestingModule({
      imports: [DialogHost, MatDialogModule, NoopAnimationsModule, translations],
    }).compileComponents();
    const fixture = TestBed.createComponent(DialogHost);
    fixture.detectChanges();
    const ref = TestBed.inject(ConflictDialog).open({
      messageKey: 'errors.concurrency_conflict',
      saveAsNewLabel: 'errors.conflict.saveAsNew',
    });
    const savedAsNew = vi.fn();
    const closed = vi.fn();
    onEmit(ref.componentInstance.saveAsNew, savedAsNew);
    onEmit(ref.afterClosed(), closed);
    await fixture.whenStable();
    const saveAsNewButton = document.querySelector<HTMLButtonElement>(
      '.cdk-overlay-pane button[data-outcome="saveAsNew"]',
    );
    if (saveAsNewButton === null) throw new Error('missing Save as new button');

    saveAsNewButton.click();
    saveAsNewButton.click();
    await fixture.whenStable();

    expect(savedAsNew).toHaveBeenCalledTimes(1);
    expect(closed).toHaveBeenCalledExactlyOnceWith('saveAsNew');
  });
});
