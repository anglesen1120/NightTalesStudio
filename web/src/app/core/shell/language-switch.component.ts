import { ChangeDetectionStrategy, Component, input, output } from '@angular/core';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatSelectModule } from '@angular/material/select';
import type { AppLocale } from '@core/i18n';
import { IconLanguage } from '@shared/ui';
import { TranslocoPipe } from '@jsverse/transloco';

@Component({
  selector: 'nts-language-switch',
  imports: [IconLanguage, MatFormFieldModule, MatSelectModule, TranslocoPipe],
  templateUrl: './language-switch.component.html',
  styleUrl: './language-switch.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class LanguageSwitchComponent {
  readonly locale = input.required<AppLocale>();
  readonly saving = input(false);
  readonly localeChange = output<AppLocale>();

  protected selectLocale(locale: AppLocale): void {
    if (!this.saving() && locale !== this.locale()) {
      this.localeChange.emit(locale);
    }
  }
}
