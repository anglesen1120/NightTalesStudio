import { ChangeDetectionStrategy, Component, afterNextRender, input, signal } from '@angular/core';
import { TranslocoPipe, provideTranslocoScope } from '@jsverse/transloco';
import { ErrorState, LoadingState } from '@shared/ui';

const QR_SIZE_PX = 208;

/**
 * The enrolment QR (deck `f-enrol-show`): `otpauthUri` -> a `data:` image through `qrcode.toDataURL`. The library is
 * loaded lazily so it never enters the initial bundle (D13, FE14); the sheet wraps this in `@defer (on viewport)`.
 * The URI carries the TOTP secret: it is rendered once as pixels and never logged, stored or put in an attribute.
 * A generation counter drops a late resolve (a retry that raced a destroy or a URI change paints nothing).
 * The explicit light colour is not a theme token: the QR needs a white quiet zone so it scans on the dark theme.
 */
@Component({
  selector: 'nts-mfa-qr',
  imports: [TranslocoPipe, ErrorState, LoadingState],
  providers: [provideTranslocoScope('auth', 'errors')],
  templateUrl: './mfa-qr.component.html',
  styleUrl: './mfa-qr.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class MfaQrComponent {
  readonly otpauthUri = input.required<string>();

  protected readonly dataUrl = signal<string | null>(null);
  protected readonly failed = signal(false);
  private generation = 0;

  constructor() {
    afterNextRender(() => void this.render());
  }

  protected retryRender(): void {
    this.failed.set(false);
    void this.render();
  }

  private async render(): Promise<void> {
    const run = ++this.generation;
    const uri = this.otpauthUri();
    try {
      // Deliberately lazy: a static import would price qrcode into the initial bundle (D13/FE14 budget).
      const { toDataURL } = await import('qrcode');
      const url = await toDataURL(uri, {
        margin: 2,
        width: QR_SIZE_PX,
        color: { dark: '#202523ff', light: '#ffffffff' },
      });
      if (run === this.generation && uri === this.otpauthUri()) this.dataUrl.set(url);
    } catch {
      if (run === this.generation) this.failed.set(true);
    }
  }
}
