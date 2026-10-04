import { ChangeDetectionStrategy, Component, input } from '@angular/core';
import { TranslocoPipe } from '@jsverse/transloco';
@Component({ selector: 'nts-icon-pause', imports: [TranslocoPipe], templateUrl: './icon-pause.html', styleUrl: './icon-pause.scss', changeDetection: ChangeDetectionStrategy.OnPush })
export class IconPause { readonly labelKey = input<string>(); }
