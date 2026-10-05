import { ChangeDetectionStrategy, Component, effect, input, output, viewChild } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatMenuModule, MatMenuTrigger } from '@angular/material/menu';
import type { MeSummary } from '@core/session';
import { TranslocoPipe } from '@jsverse/transloco';

@Component({
  selector: 'nts-user-menu',
  imports: [MatButtonModule, MatMenuModule, TranslocoPipe],
  templateUrl: './user-menu.component.html',
  styleUrl: './user-menu.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class UserMenuComponent {
  readonly me = input<MeSummary | null>(null);
  readonly userLabel = input('');
  readonly logOut = output<void>();

  private readonly menuTrigger = viewChild(MatMenuTrigger);

  constructor() {
    effect(() => {
      if (this.me() === null) {
        this.menuTrigger()?.closeMenu();
      }
    });
  }

  protected roleKey(role: string): string {
    return role === 'admin' || role === 'owner' ? `shell.menu.role.${role}` : 'shell.menu.role.user';
  }

  protected initials(name: string): string {
    const parts = name.trim().split(/\s+/u).filter(Boolean);
    return parts.slice(0, 2).map((part) => [...part][0]?.toLocaleUpperCase() ?? '').join('') || 'NT';
  }
}
