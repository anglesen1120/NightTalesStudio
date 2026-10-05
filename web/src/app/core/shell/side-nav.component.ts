import { ChangeDetectionStrategy, Component, ElementRef, HostListener, inject, input, output } from '@angular/core';
import { NgComponentOutlet } from '@angular/common';
import { MatListModule } from '@angular/material/list';
import { RouterLink, RouterLinkActive } from '@angular/router';
import { TranslocoPipe } from '@jsverse/transloco';
import type { NavItem } from './nav-items';

@Component({
  selector: 'nts-side-nav',
  imports: [MatListModule, NgComponentOutlet, RouterLink, RouterLinkActive, TranslocoPipe],
  templateUrl: './side-nav.component.html',
  styleUrl: './side-nav.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: {
    '[class.side-nav--over]': "mode() === 'over'",
    '[class.side-nav--side]': "mode() === 'side'",
  },
})
export class SideNavComponent {
  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef);
  readonly items = input<readonly NavItem[]>([]);
  readonly activeRouteId = input<string | null>(null);
  readonly mode = input<'side' | 'over'>('side');

  readonly navigate = output<void>();
  readonly menuToggle = output<void>();

  focusFirst(): void {
    const root = this.host.nativeElement;
    (root.querySelector<HTMLAnchorElement>('a[mat-list-item]') ?? root).focus();
  }

  @HostListener('keydown.escape')
  protected closeOverlay(): void {
    if (this.mode() === 'over') {
      this.menuToggle.emit();
    }
  }

  protected activate(): void {
    this.navigate.emit();
    if (this.mode() === 'over') {
      this.menuToggle.emit();
    }
  }

  protected activateFromKeyboard(event: Event): void {
    event.preventDefault();
    (event.currentTarget as HTMLElement).click();
  }
}
