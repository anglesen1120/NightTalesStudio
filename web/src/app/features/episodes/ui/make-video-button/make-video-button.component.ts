import { ChangeDetectionStrategy, Component, input, output } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { TranslocoPipe, provideTranslocoScope } from '@jsverse/transloco';

let nextMakeVideoId = 0;

@Component({
  selector: 'nts-make-video-button',
  imports: [MatButtonModule, TranslocoPipe],
  providers: [provideTranslocoScope('episodes')],
  templateUrl: './make-video-button.component.html',
  styleUrl: './make-video-button.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class MakeVideoButtonComponent {
  readonly disabled = input(false);
  readonly busy = input(false);
  readonly labelKey = input.required<string>();
  readonly reasonKey = input<string | null>(null);
  readonly request = output<void>();

  protected readonly reasonId = `nts-make-video-reason-${++nextMakeVideoId}`;

  protected onRequest(event: Event): void {
    if (this.disabled() || this.busy()) {
      event.preventDefault();
      return;
    }
    this.request.emit();
  }
}
