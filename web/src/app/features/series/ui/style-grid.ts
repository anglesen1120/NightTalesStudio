import { ChangeDetectionStrategy, Component, computed, input, output, signal } from '@angular/core';
import { TranslocoPipe, provideTranslocoScope } from '@jsverse/transloco';
import { IconCheck } from '@shared/ui';
import type { Topic } from '../domain/series.model';
import type { Language } from '../domain/voice.model';
import { styleHintKey, styleLabelKey, type StyleTemplate } from '../domain/style-template.model';
import { isTopicCompatible } from '../domain/topic-style-compatibility';
import { safePreviewUrl } from '../domain/preview-url';

let nextStyleGridId = 0;

@Component({
  selector: 'nts-style-grid',
  imports: [TranslocoPipe, IconCheck],
  providers: [provideTranslocoScope('series')],
  templateUrl: './style-grid.html',
  styleUrl: './style-grid.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class StyleGrid {
  readonly templates = input.required<readonly StyleTemplate[]>();
  readonly selectedId = input<string | null>(null);
  readonly topic = input<Topic>('ghost');
  readonly locale = input<Language>('vi');
  readonly disabled = input(false);
  readonly inactiveTemplateId = input<string | null>(null);
  readonly describedBy = input<string | null>(null);
  readonly invalid = input(false);
  readonly selected = output<string>();

  protected readonly groupName = `nts-style-group-${nextStyleGridId++}`;
  protected readonly failedUrls = signal<ReadonlySet<string>>(new Set());
  protected readonly rows = computed(() => this.templates()
    .filter((template) => isTopicCompatible(this.topic(), template) || template.id === this.inactiveTemplateId())
    .map((template) => ({
      ...template,
      labelKey: template.name === '' ? 'series.style.inactive' : styleLabelKey(template.name),
      hintKey: template.name === '' ? 'series.style.inactive' : styleHintKey(template.name),
      inactive: template.id === this.inactiveTemplateId(),
      // The upstream inactive fallback contains only an id, never a fabricated template label.
      rawLabel: template.id === this.inactiveTemplateId() && (template.name === '' || template.name === template.id),
      preview: safePreviewUrl(template.previewUrl),
    })));

  protected select(id: string): void {
    if (this.disabled() || id === this.selectedId()) return;
    if (!this.rows().some((row) => row.id === id)) return;
    this.selected.emit(id);
  }

  protected previewFailed(url: string): void {
    this.failedUrls.update((urls) => new Set([...urls, url]));
  }


  protected onGroupKeydown(event: KeyboardEvent): void {
    const delta = event.key === 'ArrowRight' || event.key === 'ArrowDown' ? 1
      : event.key === 'ArrowLeft' || event.key === 'ArrowUp' ? -1 : 0;
    if (delta === 0 || this.disabled()) return;
    const radios = Array.from((event.currentTarget as HTMLElement)
      .querySelectorAll<HTMLInputElement>('input[type="radio"]'));
    const current = radios.indexOf(event.target as HTMLInputElement);
    // Ignore arrows bubbled from nested controls, rather than stealing their focus/selection.
    if (current < 0 || radios.length === 0) return;
    event.preventDefault();
    const next = radios[(current + delta + radios.length) % radios.length];
    if (next === undefined) return;
    this.select(next.value);
    next.focus();
  }
}
