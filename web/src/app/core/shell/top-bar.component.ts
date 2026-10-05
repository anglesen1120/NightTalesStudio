import { ChangeDetectionStrategy, Component, ElementRef, TemplateRef, effect, input, output, viewChild } from '@angular/core';
import { NgTemplateOutlet } from '@angular/common';
import type { AppLocale } from '@core/i18n';
import type { RealtimeState } from '@core/realtime';
import type { MeSummary } from '@core/session';
import { IconMenu } from '@shared/ui';
import { TranslocoPipe } from '@jsverse/transloco';
import { LanguageSwitchComponent } from './language-switch.component';
import { RealtimeStatusComponent } from './realtime-status.component';
import { UserMenuComponent } from './user-menu.component';

@Component({
  selector: 'nts-top-bar',
  imports: [
    IconMenu,
    LanguageSwitchComponent,
    NgTemplateOutlet,
    RealtimeStatusComponent,
    TranslocoPipe,
    UserMenuComponent,
  ],
  templateUrl: './top-bar.component.html',
  styleUrl: './top-bar.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class TopBarComponent {
  readonly title = input('');
  readonly userLabel = input('');
  readonly me = input<MeSummary | null>(null);
  readonly locale = input.required<AppLocale>();
  readonly localeSaving = input(false);
  readonly realtimeState = input.required<RealtimeState>();
  readonly headerEnd = input<TemplateRef<unknown> | null>(null);
  readonly menuExpanded = input(false);
  readonly showMenuButton = input(false);

  readonly menuToggle = output<void>();
  readonly localeChange = output<AppLocale>();
  readonly logOut = output<void>();
  readonly resumeRequested = output<void>();

  private readonly menuButton = viewChild<ElementRef<HTMLButtonElement>>('menuButton');
  private wasMenuExpanded = false;

  constructor() {
    effect(() => {
      const expanded = this.menuExpanded();
      if (this.wasMenuExpanded && !expanded) {
        queueMicrotask(() => this.menuButton()?.nativeElement.focus());
      }
      this.wasMenuExpanded = expanded;
    });
  }
}
