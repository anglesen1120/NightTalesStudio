import { ChangeDetectionStrategy, Component, ElementRef, HostListener, effect, inject, signal, viewChild } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { BreakpointObserver } from '@angular/cdk/layout';
import { RouterOutlet } from '@angular/router';
import { ErrorState } from '@shared/ui';
import { TranslocoPipe } from '@jsverse/transloco';
import { map } from 'rxjs';
import { HeaderEndRegistry } from './header-end.registry';
import { provideShell } from './provide-shell';
import { ShellStore } from './shell.store';
import { SideNavComponent } from './side-nav.component';
import { TopBarComponent } from './top-bar.component';

@Component({
  selector: 'nts-shell',
  imports: [ErrorState, RouterOutlet, SideNavComponent, TopBarComponent, TranslocoPipe],
  providers: provideShell(),
  templateUrl: './shell.component.html',
  styleUrl: './shell.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class ShellComponent {
  protected readonly store = inject(ShellStore);
  protected readonly registry = inject(HeaderEndRegistry);
  private readonly breakpointObserver = inject(BreakpointObserver);
  private readonly navigation = viewChild(SideNavComponent);
  private readonly content = viewChild.required<ElementRef<HTMLElement>>('content');

  protected readonly isMobile = toSignal(
    this.breakpointObserver.observe('(max-width: 599px)').pipe(map((state) => state.matches)),
    { initialValue: false },
  );
  protected readonly navigationOpen = signal(false);

  constructor() {
    this.store.init();
    effect(() => {
      if (!this.isMobile() && this.navigationOpen()) {
        this.navigationOpen.set(false);
      }
    });
  }

  @HostListener('document:keydown.escape')
  protected onEscape(): void {
    this.closeNavigation();
  }

  protected toggleNavigation(): void {
    if (!this.isMobile()) return;
    if (this.navigationOpen()) {
      this.closeNavigation();
      return;
    }

    this.navigationOpen.set(true);
    queueMicrotask(() => this.navigation()?.focusFirst());
  }

  protected closeNavigation(): void {
    if (this.navigationOpen()) {
      this.navigationOpen.set(false);
    }
  }

  protected focusContent(event: Event): void {
    event.preventDefault();
    this.content().nativeElement.focus();
  }
}
