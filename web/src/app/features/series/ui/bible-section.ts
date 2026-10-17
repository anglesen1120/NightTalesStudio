import { ChangeDetectionStrategy, Component, DestroyRef, computed, effect, inject, input, output, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { Subject, takeUntil } from 'rxjs';
import { FormArray, FormControl, FormGroup, ReactiveFormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { TranslocoPipe } from '@jsverse/transloco';
import type { ServerControlError } from '@core/errors';
import { CharCounter } from '@shared/ui';

export type BibleSectionKind = 'characters' | 'places' | 'rules' | 'summaries';
export interface BibleEntryControls {
  id?: FormControl<string>;
  name?: FormControl<string>;
  description?: FormControl<string>;
  firstEpisodeId?: FormControl<string>;
  text?: FormControl<string>;
  episodeId?: FormControl<string>;
  title?: FormControl<string>;
  summary?: FormControl<string>;
}
export type BibleEntryForm = FormGroup<BibleEntryControls>;

@Component({
  selector: 'nts-bible-section',
  imports: [ReactiveFormsModule, MatButtonModule, MatFormFieldModule, MatInputModule, TranslocoPipe, CharCounter],
  templateUrl: './bible-section.html', styleUrl: './bible-section.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class BibleSection {
  readonly kind = input.required<BibleSectionKind>();
  readonly rows = input.required<FormArray<BibleEntryForm>>();
  readonly disabled = input(false);
  readonly add = output<void>();
  readonly remove = output<number>();
  protected readonly revision = signal(0);
  private readonly destroy = inject(DestroyRef);
  protected readonly addKey = computed(() => this.kind() === 'characters' ? 'series.bible.addCharacter' : this.kind() === 'places' ? 'series.bible.addPlace' : 'series.bible.addRule');
  constructor() {
    effect(onCleanup => {
      const stopped = new Subject<void>();
      void this.rows().events.pipe(takeUntil(stopped), takeUntilDestroyed(this.destroy)).forEach(() => this.revision.update(value => value + 1));
      onCleanup(() => { stopped.next(); stopped.complete(); });
    });
  }
  field(row: BibleEntryForm, name: keyof BibleEntryControls): FormControl<string> {
    const control = row.controls[name];
    if (!control) throw new Error('Missing bible form control');
    return control;
  }
  rowKey(row: BibleEntryForm): string | BibleEntryForm {
    return row.controls.id?.value || row.controls.episodeId?.value || row;
  }
  rowName(row: BibleEntryForm, index: number): string {
    return row.controls.name?.value || row.controls.title?.value || String(index + 1);
  }
  errorKey(control: FormControl<string>): string | null {
    this.revision();
    if (!control.invalid) return null;
    if (control.hasError('angleBrackets')) return 'series.bible.field.invalidName';
    const server = control.getError('server') as ServerControlError | undefined;
    if (server) return server.messageKeys[0] ?? 'series.bible.field.invalid';
    return 'series.bible.field.too_long';
  }
}
