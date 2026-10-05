import { ChangeDetectionStrategy, Component } from '@angular/core';

@Component({
  selector: 'nts-auth-page-shell',
  templateUrl: './auth-page-shell.component.html',
  styleUrl: './auth-page-shell.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class AuthPageShellComponent {}
