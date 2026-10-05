import { FormControl, FormGroup, Validators, type AbstractControl, type ValidationErrors } from '@angular/forms';
import { SERIES_TITLE_MAX, type Aspect, type Topic } from '../domain/series.model';
import type { Language } from '../domain/voice.model';

export { SERIES_TITLE_MAX };

/** The editor's typed form: one control per wire field the editor may write (09.4.1 §5). */
export type SeriesForm = FormGroup<{
  title: FormControl<string>;
  topic: FormControl<Topic>;
  language: FormControl<Language>;
  aspect: FormControl<Aspect>;
  voiceId: FormControl<string | null>;
  styleTemplateId: FormControl<string | null>;
}>;

/** The value of a whole `SeriesForm` (a non-nullable group, so no `| undefined` members). */
export interface SeriesFormValue {
  readonly title: string;
  readonly topic: Topic;
  readonly language: Language;
  readonly aspect: Aspect;
  readonly voiceId: string | null;
  readonly styleTemplateId: string | null;
}

/**
 * A title of only whitespace is empty: the server trims before it validates 1–120 (09.4.1 §5), so the client must
 * not offer a "1-character" title that is really blank. Reuses the `required` error key so `mat-error` shows the
 * usual message (the value is `''` after a trim, i.e. the same thing `Validators.required` rejects).
 */
export function trimmedNonEmpty(control: AbstractControl<string>): ValidationErrors | null {
  return control.value.trim().length === 0 ? { required: true } : null;
}

/**
 * The editor's form, built once per editor page. Defaults are the deck's: `ghost` topic checked, `landscape` aspect
 * checked, Vietnamese selected (`03-dashboard-series.html` frames `f-new-step1`/`f-new-step2`). `voiceId` and
 * `styleTemplateId` start `null` and are required — the matching steps cannot be left behind.
 */
export function createSeriesForm(): SeriesForm {
  return new FormGroup({
    title: new FormControl('', {
      nonNullable: true,
      validators: [(control: AbstractControl) => Validators.required(control), Validators.maxLength(SERIES_TITLE_MAX), trimmedNonEmpty],
    }),
    // A required validator is written as a call, never as a bare `Validators.required` reference: the repo's
    // `unbound-method` lint rule (and `this` safety) refuses the detached reference (login-page.component.ts:46).
    topic: new FormControl<Topic>('ghost', { nonNullable: true, validators: [(control: AbstractControl) => Validators.required(control)] }),
    language: new FormControl<Language>('vi', { nonNullable: true, validators: [(control: AbstractControl) => Validators.required(control)] }),
    aspect: new FormControl<Aspect>('landscape', { nonNullable: true, validators: [(control: AbstractControl) => Validators.required(control)] }),
    voiceId: new FormControl<string | null>(null, { validators: [(control: AbstractControl) => Validators.required(control)] }),
    styleTemplateId: new FormControl<string | null>(null, { validators: [(control: AbstractControl) => Validators.required(control)] }),
  });
}

/** The trimmed title the wire gets (the server trims as well; sending the trimmed value keeps the ETag stable). */
export function seriesTitleOf(form: SeriesForm): string {
  return form.controls.title.value.trim();
}
