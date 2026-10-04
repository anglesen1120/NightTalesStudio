import { ChangeDetectionStrategy, Component, input, output } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { TranslocoPipe } from '@jsverse/transloco';

@Component({
  selector: 'nts-auth-state-panel',
  imports: [MatButtonModule, TranslocoPipe],
  templateUrl: './auth-state-panel.component.html',
  styleUrl: './auth-state-panel.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class AuthStatePanelComponent {
  readonly titleKey = input.required<string>();
  readonly hintKey = input.required<string>();
  readonly actionKey = input.required<string>();
  readonly actionHref = input.required<string>();
  readonly announce = input(false);
  readonly success = input(false);

  readonly navigate = output<void>();
}
