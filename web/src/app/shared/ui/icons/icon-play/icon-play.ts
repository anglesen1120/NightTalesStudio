import { ChangeDetectionStrategy, Component, input } from '@angular/core';
import { TranslocoPipe } from '@jsverse/transloco';
@Component({ selector: 'nts-icon-play', imports: [TranslocoPipe], templateUrl: './icon-play.html', styleUrl: './icon-play.scss', changeDetection: ChangeDetectionStrategy.OnPush })
export class IconPlay { readonly labelKey = input<string>(); }
