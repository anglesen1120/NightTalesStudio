import { ChangeDetectionStrategy, Component, input } from '@angular/core';
import { TranslocoPipe } from '@jsverse/transloco';
@Component({ selector: 'nts-icon-copy', imports: [TranslocoPipe], templateUrl: './icon-copy.html', styleUrl: './icon-copy.scss', changeDetection: ChangeDetectionStrategy.OnPush })
export class IconCopy { readonly labelKey = input<string>(); }
