import { AbstractControl, FormArray, FormGroup, type ValidationErrors } from '@angular/forms';
import type { AppError } from './app-error';
import { fieldMessageKeyOf } from './error-code';

/** The control error namespace owned by the binder; every other key of `control.errors` belongs to local validators. */
const SERVER_ERROR_KEY = 'server';

/** Shape of `control.errors['server']`: the i18n keys of the server's messages, never raw text (README D9). */
export interface ServerControlError {
  readonly messageKeys: readonly string[];
}

export interface BindFieldErrorsOptions {
  /** Only `.` exists today (`segments.2.text`); kept as an option so the wire pointer syntax is one decision. */
  readonly pointerSeparator?: '.';
  /** Mark each bound control touched so its `mat-error` shows without waiting for a blur. Default `false`. */
  readonly markTouched?: boolean;
}

/** Resolves a dotted pointer to a control. `FormArray` segments are indexes; `length` and the like never resolve. */
function resolve(form: AbstractControl, pointer: string, separator: string): AbstractControl | null {
  const found = form.get(pointer.split(separator));
  return found instanceof AbstractControl ? found : null;
}

function withoutServer(errors: ValidationErrors | null): ValidationErrors | null {
  if (errors === null || !Object.hasOwn(errors, SERVER_ERROR_KEY)) return errors;
  const rest: ValidationErrors = { ...errors };
  delete rest[SERVER_ERROR_KEY];
  return Object.keys(rest).length === 0 ? null : rest;
}

/**
 * Writes the server's field errors onto the controls they name and returns the pointers that match no control
 * (de-duplicated, in payload order) for the form-level `FormErrorAlert`.
 *
 * - Pointers are dotted paths resolved with `control.get(path.split('.'))`: `title`, `segments.2.text`.
 * - Only the `server` namespace is written; local errors (`required`, `minlength`, ...) are kept as they are.
 * - Old server errors are cleared first, so binding is idempotent and a second submit replaces the first one's messages.
 * - The message is the code-specific key (`errors.<code>`), never the wire text.
 */
export function bindFieldErrors(form: AbstractControl, error: AppError, opts: BindFieldErrorsOptions = {}): string[] {
  const separator = opts.pointerSeparator ?? '.';
  clearServerErrors(form);
  const unmapped = new Set<string>();
  for (const [pointer, messages] of Object.entries(error.fieldErrors)) {
    if (messages.length === 0) continue;
    const control = resolve(form, pointer, separator);
    if (control === null) {
      unmapped.add(pointer);
      continue;
    }
    const server: ServerControlError = { messageKeys: [fieldMessageKeyOf(error, pointer)] };
    control.setErrors({ ...control.errors, [SERVER_ERROR_KEY]: server });
    if (opts.markTouched === true) control.markAsTouched();
  }
  return [...unmapped];
}

/**
 * Removes the `server` namespace from `form` and every descendant, leaving local validator errors untouched. Call it
 * before every submit; call it on a single control when the user edits that control (container-owned value-change
 * bridge). It never re-runs validators, so it cannot bring a stale server error back.
 */
export function clearServerErrors(form: AbstractControl): void {
  if (form instanceof FormGroup) {
    for (const child of Object.values(form.controls)) clearServerErrors(child);
  } else if (form instanceof FormArray) {
    for (const child of form.controls) clearServerErrors(child);
  }
  if (form.errors !== null && Object.hasOwn(form.errors, SERVER_ERROR_KEY)) {
    form.setErrors(withoutServer(form.errors));
  }
}
