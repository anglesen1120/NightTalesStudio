import { ChangeDetectionStrategy, Component, input } from '@angular/core';
import { TranslocoPipe, provideTranslocoScope } from '@jsverse/transloco';

@Component({
  selector: 'nts-revised-badge',
  imports: [TranslocoPipe],
  providers: [provideTranslocoScope('story')],
  templateUrl: './revised-badge.component.html',
  styleUrl: './revised-badge.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class RevisedBadgeComponent {
  readonly describedById = input<string | null>(null);
}
