import { ChangeDetectionStrategy, Component, computed, inject, input } from '@angular/core';
import { TranslocoPipe, provideTranslocoScope } from '@jsverse/transloco';
import { type AppError, AppErrorMapper } from '@core/errors';
import { IconAlert } from '../icons/icon-alert/icon-alert';
import { RetryCountdown } from '../retry-countdown/retry-countdown.component';
const MAX_UNMAPPED_MESSAGES = 10;

let nextFormErrorAlertId = 0;

@Component({
  selector: 'nts-form-error-alert',
  imports: [IconAlert, RetryCountdown, TranslocoPipe],
  providers: [provideTranslocoScope('errors')],
  templateUrl: './form-error-alert.component.html',
  styleUrl: './form-error-alert.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: {
    '[attr.id]': 'alertId',
    '[attr.role]': "error() === null ? null : 'alert'",
    '[attr.tabindex]': 'error() === null ? null : -1',
    '[attr.aria-atomic]': "error() === null ? null : 'true'",
  },
})
export class FormErrorAlert {
  private readonly mapper = inject(AppErrorMapper);

  readonly error = input<AppError | null>(null);
  readonly unmappedKeys = input<readonly string[]>([]);
  readonly showTrace = input(true);
  /** Stable for this component instance so form containers can use it from aria-describedby. */
  readonly alertId = `nts-form-error-alert-${++nextFormErrorAlertId}`;

  protected readonly titleKey = computed(() => {
    const error = this.error();
    return error === null ? null : this.mapper.messageKey(error);
  });

  protected readonly unmappedCount = computed(() => this.unmappedKeys().length);

  protected readonly unmappedMessages = computed(() => {
    const error = this.error();
    if (error === null) return [];

    const titleKey = this.titleKey();
    const seen = new Set<string>();
    const messageKeys: string[] = [];
    for (const pointer of this.unmappedKeys()) {
      const messageKey = this.mapper.fieldMessageKey(error, pointer);
      if (messageKey === titleKey || seen.has(messageKey)) continue;
      seen.add(messageKey);
      messageKeys.push(messageKey);
      if (messageKeys.length === MAX_UNMAPPED_MESSAGES) break;
    }
    return messageKeys;
  });

  protected readonly traceFooter = computed(() => {
    const error = this.error();
    return error !== null && this.showTrace() ? this.mapper.traceFooter(error) : null;
  });
}
