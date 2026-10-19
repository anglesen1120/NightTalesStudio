import { ChangeDetectionStrategy, Component, input, output } from '@angular/core';
import { TranslocoPipe } from '@jsverse/transloco';

@Component({
  selector: 'nts-video-decisions',
  imports: [TranslocoPipe],
  templateUrl: './video-decisions.component.html',
  styleUrl: './video-decisions.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class VideoDecisionsComponent {
  readonly canApprove = input(false);
  readonly canReject = input(false);
  readonly canDownload = input(false);
  readonly url = input<string | null>(null);
  readonly downloadName = input('nighttales.mp4');
  readonly busy = input(false);
  readonly showDecisions = input(true);
  readonly downloadErrorKey = input<string | null>(null);
  readonly approveRequest = output<void>();
  readonly rejectRequest = output<void>();
  readonly reMakeRequest = output<void>();
  readonly downloadClick = output<void>();

  protected request(event: Event, action: 'approve' | 'reject' | 'reMake'): void {
    if (this.busy() || !(action === 'approve' ? this.canApprove() : this.canReject())) {
      event.preventDefault();
      return;
    }
    if (action === 'approve') this.approveRequest.emit();
    else if (action === 'reject') this.rejectRequest.emit();
    else this.reMakeRequest.emit();
  }
}
