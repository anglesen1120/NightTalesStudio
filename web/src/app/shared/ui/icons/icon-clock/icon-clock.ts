import { ChangeDetectionStrategy, Component, input } from '@angular/core';
import { TranslocoPipe } from '@jsverse/transloco';
@Component({ selector: 'nts-icon-clock', imports: [TranslocoPipe], templateUrl: './icon-clock.html', styleUrl: './icon-clock.scss', changeDetection: ChangeDetectionStrategy.OnPush })
export class IconClock { readonly labelKey = input<string>(); }
