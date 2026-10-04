import { TemplateRef, ViewChild, Component, inject } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { MatDialogModule, MatDialogRef } from '@angular/material/dialog';
import { NoopAnimationsModule } from '@angular/platform-browser/animations';
import { describe, expect, it, vi } from 'vitest';
import { uiTestingTranslations } from '../testing/ui-testing-translations';
import { ConfirmDialog, ConfirmDialogComponent, type ConfirmDialogTemplateContext } from './confirm-dialog';

@Component({
  imports: [ConfirmDialogComponent],
  template: `<nts-confirm-dialog titleKey="core.ui.confirm.title" bodyKey="core.ui.errorState.body" confirmLabelKey="core.ui.confirm.confirm" [pending]="pending" (confirmed)="confirmed()"><span cost-line>cost line</span></nts-confirm-dialog>`,
})
class StaticHost { pending = false; confirmed = vi.fn(); }

@Component({
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
  it('renders static cost content and disables every action while pending', async () => {
    await TestBed.configureTestingModule({ imports: [StaticHost, MatDialogModule, NoopAnimationsModule, uiTestingTranslations()] }).compileComponents();
    const fixture = TestBed.createComponent(StaticHost);
    fixture.componentInstance.pending = true;
    fixture.detectChanges();
    await fixture.whenStable();
    expect(fixture.nativeElement.textContent).toContain('cost line');
    expect([...fixture.nativeElement.querySelectorAll('button')].every((button: HTMLButtonElement) => button.disabled)).toBe(true);
    expect(fixture.nativeElement.querySelector('[aria-modal="true"]')).not.toBeNull();
  });

  it('traps focus, renders typed dynamic cost, cancels on Escape, destroys content and restores focus', async () => {
    await TestBed.configureTestingModule({ imports: [DynamicHost, MatDialogModule, NoopAnimationsModule, uiTestingTranslations()] }).compileComponents();
    const fixture = TestBed.createComponent(DynamicHost);
    fixture.detectChanges();
    const trigger = fixture.nativeElement.querySelector('#trigger') as HTMLButtonElement;
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
    ref.afterClosed().subscribe(closed);
    await fixture.whenStable();
    const overlay = document.querySelector('.cdk-overlay-pane') as HTMLElement;
    const buttons = [...overlay.querySelectorAll('button')] as HTMLButtonElement[];
    expect(buttons.length).toBe(3);
    expect(buttons.every((button) => button.disabled)).toBe(true);
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
      ref.afterClosed().subscribe(closed);
      await fixture.whenStable();
      const buttons = [...document.querySelectorAll('.cdk-overlay-pane .confirm-dialog__actions button')] as HTMLButtonElement[];
      const target = buttons[index - 1];
      expect(target).toBeDefined();
      target?.click();
      await fixture.whenStable();
      expect(closed).toHaveBeenCalledWith(expected);
    }
  });
});
