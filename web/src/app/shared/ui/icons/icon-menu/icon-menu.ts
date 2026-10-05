import { ChangeDetectionStrategy, Component } from '@angular/core';

@Component({
  selector: 'nts-icon-menu',
  templateUrl: './icon-menu.html',
  styleUrl: './icon-menu.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class IconMenu {}
