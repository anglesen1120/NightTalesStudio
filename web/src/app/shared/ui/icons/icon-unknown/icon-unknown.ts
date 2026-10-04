import { ChangeDetectionStrategy, Component, input } from '@angular/core';
import { TranslocoPipe } from '@jsverse/transloco';
@Component({ selector: 'nts-icon-unknown', imports: [TranslocoPipe], templateUrl: './icon-unknown.html', styleUrl: './icon-unknown.scss', changeDetection: ChangeDetectionStrategy.OnPush })
export class IconUnknown { readonly labelKey = input<string>(); }
