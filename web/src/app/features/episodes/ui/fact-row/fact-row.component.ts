import { ChangeDetectionStrategy, Component, computed, input, output, type OnChanges } from '@angular/core';
import { FormControl, FormGroup, ReactiveFormsModule, Validators } from '@angular/forms';
import { bindFieldErrors, clearServerErrors, type AppError } from '@core/errors';
import { MatSelectModule } from '@angular/material/select';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatTooltipModule } from '@angular/material/tooltip';
import { TranslocoPipe } from '@jsverse/transloco';
import { CharCounter, IconAlert } from '@shared/ui';
import { FACT_STATEMENT_MAX_CHARS, isSafeHttpUrl, type Fact, type FactConfidence, type ResearchSource } from '../../domain/research.model';
export type FactPatch = Partial<Pick<Fact, 'statement' | 'confidence' | 'sourceIds'>>;
let nextFactRowId = 0;
@Component({ selector: 'nts-fact-row', imports: [ReactiveFormsModule, TranslocoPipe, MatFormFieldModule, MatSelectModule, MatTooltipModule, CharCounter, IconAlert], templateUrl: './fact-row.component.html', styleUrl: './fact-row.component.scss', changeDetection: ChangeDetectionStrategy.OnPush })
export class FactRowComponent implements OnChanges {
  readonly fact = input.required<Fact>();
  readonly index = input.required<number>();
  readonly sources = input<readonly ResearchSource[]>([]);
  readonly readonly = input(false);
  readonly busy = input(false);
  readonly invalid = input(false);
  readonly fieldError = input<AppError | null>(null);
  readonly form = new FormGroup({
    statement: new FormControl('', { nonNullable: true, validators: [control => Validators.required(control), Validators.maxLength(FACT_STATEMENT_MAX_CHARS)] }),
    confidence: new FormControl<FactConfidence>('high', { nonNullable: true }),
    sourceIds: new FormControl<readonly string[]>([], { nonNullable: true, validators: [control => Validators.required(control)] }),
  });
  readonly patch = output<FactPatch>();
  readonly deleteFact = output<void>();
  readonly dropSource = output<string>();
  readonly fieldId = 'research-fact-' + ++nextFactRowId;
  readonly max = FACT_STATEMENT_MAX_CHARS;
  readonly isSafeUrl = isSafeHttpUrl;
  readonly overlapPercent = computed(() => Math.round((this.fact().sourceOverlap ?? 0) * 100) + '%');
  readonly refs = computed(() => this.fact().sourceIds.map((id, index) => {
    const ref = this.fact().sources.find(item => item.sourceId === id);
    const source = this.sources().find(item => item.id === id);
    return { sourceId: id, alias: ref?.alias ?? 'S' + (index + 1), title: ref?.title || source?.title || source?.registrableDomain || id, url: ref?.url ?? source?.url ?? null, isUserNote: ref?.isUserNote ?? source?.kind === 'user_note', missing: source === undefined };
  }));
  ngOnChanges(): void {
    const fact = this.fact();
    this.form.setValue({ statement: fact.statement, confidence: fact.confidence, sourceIds: fact.sourceIds }, { emitEvent: false });
    if (this.readonly() || this.busy()) this.form.disable({ emitEvent: false }); else this.form.enable({ emitEvent: false });
    const error = this.fieldError();
    if (!error) { clearServerErrors(this.form); return; }
    const fields: Record<string, readonly string[]> = {};
    for (const [path, messages] of Object.entries(error.fieldErrors)) {
      const match = /(?:content\.)?facts(?:\.|\[)(\d+)\]?\.(statement|confidence|sourceIds)/i.exec(path);
      if (match?.[1] && match[2] && Number(match[1]) === this.index() - 1) fields[match[2]] = messages;
    }
    bindFieldErrors(this.form, { ...error, fieldErrors: fields });
  }
  changeStatement(event: Event): void {
    const target = event.target;
    if (target instanceof HTMLTextAreaElement && !this.readonly() && !this.busy()) {
      this.form.controls.statement.setValue(target.value, { emitEvent: false });
      clearServerErrors(this.form); this.patch.emit({ statement: this.form.controls.statement.getRawValue() });
    }
  }
  changeConfidence(value: unknown): void {
    if (!this.readonly() && !this.busy() && (value === 'high' || value === 'medium' || value === 'low')) {
      clearServerErrors(this.form); this.patch.emit({ confidence: value });
    }
  }
  changeSources(value: unknown): void {
    if (!this.readonly() && !this.busy() && Array.isArray(value) && value.every((id: unknown) => typeof id === 'string')) {
      clearServerErrors(this.form); this.patch.emit({ sourceIds: value });
    }
  }
}
