import { ChangeDetectionStrategy, TemplateRef, ViewChild, Component, inject, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { MatDialogModule, MatDialogRef } from '@angular/material/dialog';
import { NoopAnimationsModule } from '@angular/platform-browser/animations';
import { describe, expect, it, vi } from 'vitest';
import { uiTestingTranslations } from '../testing/ui-testing-translations';
import { onEmit } from '../testing/on-emit';
import { ConfirmDialog, ConfirmDialogComponent, type ConfirmDialogTemplateContext } from './confirm-dialog';

@Component({
  imports: [ConfirmDialogComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `<nts-confirm-dialog titleKey="core.ui.confirm.title" bodyKey="core.ui.errorState.body" confirmLabelKey="core.ui.confirm.confirm" [pending]="pending" (confirmed)="confirmed()"><span cost-line>cost line</span></nts-confirm-dialog>`,
})
class StaticHost { pending = false; confirmed = vi.fn(); }

@Component({
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `<button id="trigger" type="button" (click)="open()">open</button><ng-template #cost let-pending="pending"><span id="dynamic-cost">cost {{ pending }}</span></ng-template>`,
})
class DynamicHost {
  private readonly dialogs = inject(ConfirmDialog);
  @ViewChild('cost', { static: true }) cost!: TemplateRef<ConfirmDialogTemplateContext>;
  ref?: MatDialogRef<ConfirmDialogComponent, boolean>;
  open(): void {
    this.ref = this.dialogs.confirm({ titleKey: 'core.ui.confirm.title', bodyKey: 'core.ui.errorState.body', confirmLabelKey: 'core.ui.confirm.confirm' }, { costTemplate: this.cost });
  }
}

describe('ConfirmDialog', () => {
  it('renders static cost content and marks every action aria-disabled while pending', async () => {
    await TestBed.configureTestingModule({ imports: [StaticHost, MatDialogModule, NoopAnimationsModule, uiTestingTranslations()] }).compileComponents();
    const fixture = TestBed.createComponent(StaticHost);
    fixture.componentInstance.pending = true;
    fixture.detectChanges();
    await fixture.whenStable();
    const root = fixture.nativeElement as HTMLElement;
    expect(root.textContent).toContain('cost line');
    expect(Array.from(root.querySelectorAll<HTMLButtonElement>('button')).every((button) => button.getAttribute('aria-disabled') === 'true' && !button.disabled)).toBe(true);
    expect(root.querySelector('[aria-modal="true"]')).not.toBeNull();
  });

  it('traps focus, renders typed dynamic cost, cancels on Escape, destroys content and restores focus', async () => {
    await TestBed.configureTestingModule({ imports: [DynamicHost, MatDialogModule, NoopAnimationsModule, uiTestingTranslations()] }).compileComponents();
    const fixture = TestBed.createComponent(DynamicHost);
    fixture.detectChanges();
    const trigger = (fixture.nativeElement as HTMLElement).querySelector<HTMLButtonElement>('#trigger');
    if (trigger === null) throw new Error('missing trigger');
    trigger.focus();
    trigger.click();
    await fixture.whenStable();
    const overlay = document.querySelector('.cdk-overlay-pane') as HTMLElement;
    expect(overlay.querySelector('#dynamic-cost')?.textContent).toContain('false');
    expect(overlay.querySelectorAll('.cdk-focus-trap-anchor').length).toBeGreaterThanOrEqual(2);
    (overlay.querySelector('.confirm-dialog') as HTMLElement).dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true }));
    await fixture.whenStable();
    expect(document.querySelector('#dynamic-cost')).toBeNull();
    expect(document.activeElement).toBe(trigger);
  });

  it('ignores Escape, the close button, cancel and confirm while a dialog opened pending', async () => {
    await TestBed.configureTestingModule({ imports: [DynamicHost, MatDialogModule, NoopAnimationsModule, uiTestingTranslations()] }).compileComponents();
    const fixture = TestBed.createComponent(DynamicHost);
    fixture.detectChanges();
    const dialogs = TestBed.inject(ConfirmDialog);
    const ref = dialogs.confirm({ titleKey: 'core.ui.confirm.title', bodyKey: 'core.ui.errorState.body', confirmLabelKey: 'core.ui.confirm.confirm', pending: true });
    const closed = vi.fn();
    onEmit(ref.afterClosed(), closed);
    await fixture.whenStable();
    const overlay = document.querySelector('.cdk-overlay-pane') as HTMLElement;
    const buttons = [...overlay.querySelectorAll('button')] as HTMLButtonElement[];
    expect(buttons.length).toBe(3);
    expect(buttons.every((button) => button.getAttribute('aria-disabled') === 'true' && !button.disabled)).toBe(true);
    expect(overlay.querySelector('.confirm-dialog__pending')?.textContent).toContain('Đang xử lý');
    (overlay.querySelector('.confirm-dialog') as HTMLElement).dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true }));
    document.body.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', keyCode: 27, bubbles: true }));
    buttons.forEach((button) => button.click());
    await fixture.whenStable();
    expect(closed).not.toHaveBeenCalled();
    expect(document.querySelector('.confirm-dialog')).not.toBeNull();
    ref.close();
    await fixture.whenStable();
  });

  it('resolves false on cancel and true on confirm for a non-pending dialog', async () => {
    await TestBed.configureTestingModule({ imports: [DynamicHost, MatDialogModule, NoopAnimationsModule, uiTestingTranslations()] }).compileComponents();
    const fixture = TestBed.createComponent(DynamicHost);
    fixture.detectChanges();
    const dialogs = TestBed.inject(ConfirmDialog);
    const data = { titleKey: 'core.ui.confirm.title', bodyKey: 'core.ui.errorState.body', confirmLabelKey: 'core.ui.confirm.confirm' };
    for (const [index, expected] of [[1, false], [2, true]] as const) {
      const ref = dialogs.confirm(data);
      const closed = vi.fn();
      onEmit(ref.afterClosed(), closed);
      await fixture.whenStable();
      const buttons = [...document.querySelectorAll('.cdk-overlay-pane .confirm-dialog__actions button')] as HTMLButtonElement[];
      const target = buttons[index - 1];
      expect(target).toBeDefined();
      target?.click();
      await fixture.whenStable();
      expect(closed).toHaveBeenCalledWith(expected);
    }
  });

  describe('pending lifetime, cancel semantics, focus and ARIA', () => {
    const data = { titleKey: 'core.ui.confirm.title', bodyKey: 'core.ui.errorState.body', confirmLabelKey: 'core.ui.confirm.confirm' };
    const escape = (): void => {
      document.body.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', keyCode: 27, bubbles: true }));
    };
    const backdrop = (): void => {
      (document.querySelector('.cdk-overlay-backdrop') as HTMLElement).click();
    };
    const setup = async () => {
      await TestBed.configureTestingModule({ imports: [DynamicHost, MatDialogModule, NoopAnimationsModule, uiTestingTranslations()] }).compileComponents();
      const fixture = TestBed.createComponent(DynamicHost);
      fixture.detectChanges();
      return { fixture, dialogs: TestBed.inject(ConfirmDialog) };
    };

    it('blocks Escape and backdrop when pending flips true after open, and works again once it flips back', async () => {
      const { fixture, dialogs } = await setup();
      const pending = signal(false);
      const ref = dialogs.confirm({ ...data, pending });
      const closed = vi.fn();
      const cancelled = vi.fn();
      onEmit(ref.afterClosed(), closed);
      await fixture.whenStable();
      onEmit(ref.componentInstance.cancelled, cancelled);

      pending.set(true);
      fixture.detectChanges();
      await fixture.whenStable();
      escape();
      backdrop();
      await fixture.whenStable();
      expect(closed).not.toHaveBeenCalled();
      expect(cancelled).not.toHaveBeenCalled();
      expect(document.querySelector('.confirm-dialog')).not.toBeNull();

      pending.set(false);
      fixture.detectChanges();
      escape();
      await fixture.whenStable();
      expect(closed).toHaveBeenCalledExactlyOnceWith(false);
      expect(cancelled).toHaveBeenCalledTimes(1);
    });

    it('keeps focus on Confirm when pending flips true, ignores click and Enter, and works again once it flips back', async () => {
      const { fixture, dialogs } = await setup();
      const pending = signal(false);
      const ref = dialogs.confirm({ ...data, pending });
      const closed = vi.fn();
      const confirmed = vi.fn();
      onEmit(ref.afterClosed(), closed);
      await fixture.whenStable();
      onEmit(ref.componentInstance.confirmed, confirmed);
      const [, confirm] = [...document.querySelectorAll('.cdk-overlay-pane .confirm-dialog__actions button')] as HTMLButtonElement[];
      expect(confirm).toBeDefined();
      confirm!.focus();
      expect(document.activeElement).toBe(confirm);

      pending.set(true);
      fixture.detectChanges();
      await fixture.whenStable();
      expect(document.activeElement).toBe(confirm);
      expect(confirm!.getAttribute('aria-disabled')).toBe('true');
      expect(confirm!.disabled).toBe(false);
      confirm!.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', bubbles: true }));
      confirm!.click();
      await fixture.whenStable();
      expect(confirmed).not.toHaveBeenCalled();
      expect(closed).not.toHaveBeenCalled();
      expect(document.activeElement).toBe(confirm);

      pending.set(false);
      fixture.detectChanges();
      await fixture.whenStable();
      expect(confirm!.hasAttribute('aria-disabled')).toBe(false);
      confirm!.click();
      await fixture.whenStable();
      expect(confirmed).toHaveBeenCalledTimes(1);
      expect(closed).toHaveBeenCalledExactlyOnceWith(true);
    });

    it.each([
      ['Escape', escape],
      ['backdrop click', backdrop],
    ])('resolves false and fires cancelled on %s for a non-pending dialog', async (_label, dismiss) => {
      const { fixture, dialogs } = await setup();
      const ref = dialogs.confirm(data);
      const closed = vi.fn();
      const cancelled = vi.fn();
      const confirmed = vi.fn();
      onEmit(ref.afterClosed(), closed);
      await fixture.whenStable();
      onEmit(ref.componentInstance.cancelled, cancelled);
      onEmit(ref.componentInstance.confirmed, confirmed);
      dismiss();
      await fixture.whenStable();
      expect(closed).toHaveBeenCalledExactlyOnceWith(false);
      expect(cancelled).toHaveBeenCalledTimes(1);
      expect(confirmed).not.toHaveBeenCalled();
    });

    it('puts initial focus on Cancel for a destructive dialog only', async () => {
      const { fixture, dialogs } = await setup();
      // jsdom has no layout: CDK's isVisible() needs geometry, otherwise nothing counts as focusable.
      const geometry = vi.spyOn(HTMLElement.prototype, 'getClientRects').mockReturnValue([{}] as unknown as DOMRectList);
      try {
        const destructive = dialogs.confirm({ ...data, destructive: true });
        await fixture.whenStable();
        const [cancel] = [...document.querySelectorAll('.cdk-overlay-pane .confirm-dialog__actions button')] as HTMLButtonElement[];
        await vi.waitFor(() => expect(document.activeElement).toBe(cancel));
        destructive.close();
        await fixture.whenStable();

        const plain = dialogs.confirm(data);
        await fixture.whenStable();
        const [plainCancel] = [...document.querySelectorAll('.cdk-overlay-pane .confirm-dialog__actions button')] as HTMLButtonElement[];
        expect(plainCancel?.hasAttribute('cdkFocusInitial')).toBe(false);
        expect(document.activeElement).not.toBe(plainCancel);
        plain.close();
        await fixture.whenStable();
      } finally {
        geometry.mockRestore();
      }
    });

    it('gives two open dialogs distinct title/body ids wired to their own container', async () => {
      const { fixture, dialogs } = await setup();
      const first = dialogs.confirm(data);
      const second = dialogs.confirm(data);
      await fixture.whenStable();
      const panes = [...document.querySelectorAll('.cdk-overlay-pane')] as HTMLElement[];
      expect(panes.length).toBe(2);
      const ids = panes.flatMap((pane) => [pane.querySelector('h2')!.id, pane.querySelector('.confirm-dialog > p')!.id]);
      expect(ids.every((id) => id.length > 0)).toBe(true);
      expect(new Set(ids).size).toBe(4);
      for (const pane of panes) {
        const container = pane.querySelector('[role="dialog"]') as HTMLElement;
        expect(container.getAttribute('aria-labelledby')).toBe(pane.querySelector('h2')!.id);
        expect(container.getAttribute('aria-describedby')).toBe(pane.querySelector('.confirm-dialog > p')!.id);
      }
      first.close();
      second.close();
      await fixture.whenStable();
    });

    it('renders exactly one role=dialog with aria-modal in the overlay', async () => {
      const { fixture, dialogs } = await setup();
      const ref = dialogs.confirm(data);
      await fixture.whenStable();
      const dialogsInOverlay = document.querySelectorAll('.cdk-overlay-container [role="dialog"]');
      expect(dialogsInOverlay.length).toBe(1);
      expect(dialogsInOverlay[0]?.getAttribute('aria-modal')).toBe('true');
      expect(document.querySelector('.confirm-dialog')?.hasAttribute('role')).toBe(false);
      ref.close();
      await fixture.whenStable();
    });
  });
});
