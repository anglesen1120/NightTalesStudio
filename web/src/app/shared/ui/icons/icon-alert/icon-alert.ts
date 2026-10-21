import { ChangeDetectionStrategy, Component, input } from '@angular/core';
import { TranslocoPipe } from '@jsverse/transloco';
@Component({ selector: 'nts-icon-alert', imports: [TranslocoPipe], templateUrl: './icon-alert.html', styleUrl: './icon-alert.scss', changeDetection: ChangeDetectionStrategy.OnPush })
export class IconAlert { readonly labelKey = input<string>(); }
