import { ChangeDetectionStrategy, Component, computed, inject, input } from '@angular/core';
import { TranslocoPipe } from '@jsverse/transloco';
import { LocaleService } from '@core/i18n/locale.service';
import type { FinalVideoAsset } from '../../domain/episode.model';

@Component({
  selector: 'nts-final-video-meta',
  imports: [TranslocoPipe],
  templateUrl: './final-video-meta.component.html',
  styleUrl: './final-video-meta.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class FinalVideoMetaComponent {
  readonly finalVideo = input<FinalVideoAsset | null>(null);
  readonly expiresAt = input<string | null>(null);
  private readonly locale = inject(LocaleService);
  protected readonly size = computed(() => {
    const asset = this.finalVideo();
    return asset ? this.locale.formatNumber(asset.sizeBytes / 1_000_000, { style: 'unit', unit: 'megabyte', maximumFractionDigits: 1 }) : '';
  });
  protected readonly duration = computed(() => {
    const asset = this.finalVideo();
    return asset && asset.durationSec !== null ? this.locale.formatNumber(asset.durationSec, { style: 'unit', unit: 'second', maximumFractionDigits: 1 }) : null;
  });
  protected readonly retention = computed(() => {
    const value = this.finalVideo()?.retentionUntil;
    return value && Number.isFinite(Date.parse(value)) ? this.locale.formatDateTime(value) : null;
  });
  protected readonly expiry = computed(() => {
    const value = this.expiresAt();
    return value && Number.isFinite(Date.parse(value)) ? this.locale.formatDateTime(value) : null;
  });
}
