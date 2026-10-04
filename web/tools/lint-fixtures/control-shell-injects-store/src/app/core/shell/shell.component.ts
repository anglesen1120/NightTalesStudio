import { ChangeDetectionStrategy, Component, inject } from '@angular/core';

class SessionStore {}

@Component({
  selector: 'app-shell',
  template: '',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class ShellComponent {
  readonly session = inject(SessionStore);
}
