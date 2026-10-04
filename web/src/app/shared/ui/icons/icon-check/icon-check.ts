import { ChangeDetectionStrategy, Component, input } from '@angular/core';
import { TranslocoPipe } from '@jsverse/transloco';
@Component({ selector: 'nts-icon-check', imports: [TranslocoPipe], templateUrl: './icon-check.html', styleUrl: './icon-check.scss', changeDetection: ChangeDetectionStrategy.OnPush })
export class IconCheck { readonly labelKey = input<string>(); }
