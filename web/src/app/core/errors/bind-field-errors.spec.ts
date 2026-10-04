import { ChangeDetectionStrategy, Component, computed, signal } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { TestBed, type ComponentFixture } from '@angular/core/testing';
import {
  FormArray,
  FormControl,
  FormGroup,
  ReactiveFormsModule,
  Validators,
  type AbstractControl,
  type ValidationErrors,
} from '@angular/forms';
import { Observable, merge, scan, tap } from 'rxjs';
import { ZONELESS_TEST_PROVIDERS } from '../../testing/providers';
import type { AppError } from './app-error';
import { bindFieldErrors, clearServerErrors, type ServerControlError } from './bind-field-errors';

/** A hand-written `AppError`; the binder reads only `code` and `fieldErrors`. */
function errorWith(fieldErrors: Record<string, string[]>, code = 'validation_failed'): AppError {
  return { code, status: 400, kind: 'validation', fieldErrors };
}

function segment(): FormGroup<{ text: FormControl<string> }> {
  return new FormGroup({ text: new FormControl('', { nonNullable: true }) });
}

function storyForm() {
  return new FormGroup({
    title: new FormControl('', {
      nonNullable: true,
      validators: [(control: AbstractControl) => Validators.required(control), Validators.minLength(3)],
    }),
    segments: new FormArray([segment(), segment(), segment()]),
  });
}

function serverOf(control: AbstractControl | null): ServerControlError | undefined {
  const errors: ValidationErrors | null | undefined = control?.errors;
  return errors?.['server'] as ServerControlError | undefined;
}

describe('bindFieldErrors', () => {
  it('BindFieldErrors_MapsNestedPointer_ToControl', () => {
    const form = storyForm();
    const unmapped = bindFieldErrors(form, errorWith({ title: ['title_too_short'], 'segments.2.text': ['text_required'] }));
    expect(unmapped).toEqual([]);
    expect(serverOf(form.controls.title)).toEqual({ messageKeys: ['errors.validation_failed'] });
    expect(serverOf(form.controls.segments.at(2).controls.text)).toEqual({ messageKeys: ['errors.validation_failed'] });
    expect(form.controls.segments.at(0).controls.text.errors).toBeNull();
    expect(form.controls.segments.at(1).controls.text.errors).toBeNull();
    // the invalid leaf makes every ancestor invalid
    expect(form.controls.segments.invalid).toBe(true);
    expect(form.invalid).toBe(true);
  });

  it('BindFieldErrors_UsesTheCodeSpecificKey_NeverWireText', () => {
    const form = storyForm();
    bindFieldErrors(form, errorWith({ 'segments.1.text': ['SECRET wire text'] }, 'story_invalid_content'));
    expect(serverOf(form.controls.segments.at(1).controls.text)).toEqual({ messageKeys: ['errors.story_invalid_content'] });
    expect(JSON.stringify(form.errors ?? {})).not.toContain('SECRET');
    expect(JSON.stringify(form.controls.segments.at(1).controls.text.errors)).not.toContain('SECRET');
  });

  it('BindFieldErrors_ReturnsUnmappedKeys', () => {
    const form = storyForm();
    const error = errorWith({ unknownField: ['x'], title: ['y'], 'segments.7.text': ['z'] });
    expect(bindFieldErrors(form, error)).toEqual(['unknownField', 'segments.7.text']);
    // binding is idempotent: the same payload gives the same list, never a duplicate
    expect(bindFieldErrors(form, error)).toEqual(['unknownField', 'segments.7.text']);
    expect(serverOf(form.controls.title)?.messageKeys).toEqual(['errors.validation_failed']);
  });

  it.each(['', 'segments.length', 'segments.x', 'segments..text', '.title', 'title.extra', '__proto__.x', 'constructor', 'segments.0', 'toString'])(
    'BindFieldErrors_UnresolvablePointer_%j_IsUnmapped_AndNeverBinds',
    (pointer) => {
      const form = storyForm();
      const unmapped = bindFieldErrors(form, errorWith({ [pointer]: ['x'] }));
      if (pointer === 'segments.0') {
        // a pointer may name a container: the segment group itself carries the server error
        expect(unmapped).toEqual([]);
        expect(serverOf(form.controls.segments.at(0))?.messageKeys).toEqual(['errors.validation_failed']);
        return;
      }
      expect(unmapped).toEqual([pointer]);
      expect(form.errors).toBeNull();
      expect(serverOf(form.controls.title)).toBeUndefined();
    },
  );

  it('BindFieldErrors_IgnoresEmptyMessageLists', () => {
    const form = storyForm();
    expect(bindFieldErrors(form, errorWith({ title: [], ghost: [] }))).toEqual([]);
    expect(serverOf(form.controls.title)).toBeUndefined();
  });

  it('BindFieldErrors_MarkTouched_IsOptIn', () => {
    const form = storyForm();
    bindFieldErrors(form, errorWith({ title: ['x'] }));
    expect(form.controls.title.touched).toBe(false);
    bindFieldErrors(form, errorWith({ title: ['x'] }), { markTouched: true });
    expect(form.controls.title.touched).toBe(true);
    expect(form.controls.segments.touched).toBe(false);
  });

  it('BindFieldErrors_KeepsLocalErrors_ReplacesOnlyServerNamespace', () => {
    const form = storyForm();
    form.controls.title.setValue('ab');
    expect(Object.keys(form.controls.title.errors ?? {})).toEqual(['minlength']);
    bindFieldErrors(form, errorWith({ title: ['x'] }));
    expect(Object.keys(form.controls.title.errors ?? {}).sort()).toEqual(['minlength', 'server']);
    // a second binding replaces the server messages, it does not stack them
    bindFieldErrors(form, errorWith({ title: ['y'] }, 'story_invalid_content'));
    expect(form.controls.title.errors?.['minlength']).toBeDefined();
    expect(serverOf(form.controls.title)).toEqual({ messageKeys: ['errors.story_invalid_content'] });
    // a control the second payload no longer names loses its server error
    bindFieldErrors(form, errorWith({ 'segments.0.text': ['z'] }));
    expect(Object.keys(form.controls.title.errors ?? {})).toEqual(['minlength']);
    expect(serverOf(form.controls.segments.at(0).controls.text)).toBeDefined();
  });

  it('BindFieldErrors_DoesNotMutateTheErrorsObjectInPlace', () => {
    const form = storyForm();
    form.controls.title.setValue('ab');
    const before = form.controls.title.errors;
    bindFieldErrors(form, errorWith({ title: ['x'] }));
    expect(before).toEqual({ minlength: { requiredLength: 3, actualLength: 2 } });
    expect(form.controls.title.errors).not.toBe(before);
  });
});

describe('clearServerErrors', () => {
  it('removes only the server namespace, recursively, and restores validity', () => {
    const form = storyForm();
    form.controls.title.setValue('abcd');
    bindFieldErrors(form, errorWith({ title: ['x'], 'segments.1.text': ['y'] }));
    expect(form.invalid).toBe(true);
    clearServerErrors(form);
    expect(form.controls.title.errors).toBeNull();
    expect(form.controls.segments.at(1).controls.text.errors).toBeNull();
    expect(form.controls.segments.errors).toBeNull();
    expect(form.valid).toBe(true);
  });

  it('keeps local validator errors on the same control', () => {
    const form = storyForm();
    bindFieldErrors(form, errorWith({ title: ['x'] }));
    clearServerErrors(form);
    expect(Object.keys(form.controls.title.errors ?? {})).toEqual(['required']);
  });

  it('is idempotent, and clearing one control leaves its siblings alone', () => {
    const form = storyForm();
    bindFieldErrors(form, errorWith({ title: ['x'], 'segments.0.text': ['y'] }));
    clearServerErrors(form.controls.title);
    clearServerErrors(form.controls.title);
    expect(serverOf(form.controls.title)).toBeUndefined();
    expect(serverOf(form.controls.segments.at(0).controls.text)).toBeDefined();
  });

  it('never installs anything: a form without server errors is unchanged', () => {
    const form = storyForm();
    clearServerErrors(form);
    expect(form.errors).toBeNull();
    expect(form.controls.segments.errors).toBeNull();
  });
});

// ------------------------------------------------------------------------------------------------------------------
// BindFieldErrors_PreservesLocalAndClearsServerLifecycle: a container owns the value-change bridge.
// ------------------------------------------------------------------------------------------------------------------

let activeBridges = 0;

/** Counts live subscriptions so the spec can prove the bridge is released with the component (DestroyRef). */
function counted<T>(source: Observable<T>): Observable<T> {
  return new Observable<T>((subscriber) => {
    activeBridges++;
    const inner = source.subscribe(subscriber);
    return () => {
      activeBridges--;
      inner.unsubscribe();
    };
  });
}

@Component({
  selector: 'nts-test-story-form-host',
  imports: [ReactiveFormsModule],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <form [formGroup]="form">
      <input id="title" formControlName="title" />
      <ul id="title-messages">
        @for (message of messages('title'); track $index) {
          <li>{{ message }}</li>
        }
      </ul>
      <div formArrayName="segments">
        @for (segmentControl of form.controls.segments.controls; track segmentControl) {
          <div [formGroupName]="$index">
            <input [id]="'text-' + $index" formControlName="text" />
            <ul [id]="'text-messages-' + $index">
              @for (message of messages('segments.' + $index + '.text'); track $index) {
                <li>{{ message }}</li>
              }
            </ul>
          </div>
        }
      </div>
      <p id="unmapped">{{ unmapped().join(',') }}</p>
      <button id="submit" type="button" [disabled]="invalid()">Save</button>
    </form>
  `,
})
class StoryFormHostComponent {
  readonly form = storyForm();
  readonly unmapped = signal<readonly string[]>([]);

  // Status and value changes reach the template through one signal; toSignal releases the subscription with the
  // component's DestroyRef, so there is no manual subscribe and no timer patch.
  private readonly version = toSignal(
    counted(merge(this.form.statusChanges, this.form.valueChanges)).pipe(scan((count) => count + 1, 0)),
    { initialValue: 0 },
  );
  // Edit clears only the edited control's server namespace (the container-owned bridge).
  private readonly titleEdits = toSignal(
    counted(this.form.controls.title.valueChanges).pipe(tap(() => clearServerErrors(this.form.controls.title))),
    { initialValue: '' },
  );
  private readonly textEdits = this.form.controls.segments.controls.map((segmentGroup) =>
    toSignal(
      counted(segmentGroup.controls.text.valueChanges).pipe(tap(() => clearServerErrors(segmentGroup.controls.text))),
      { initialValue: '' },
    ),
  );
  readonly invalid = computed(() => {
    this.version();
    return this.form.invalid;
  });

  /** One line per message: the local validator name, or the server message key. */
  messages(path: string): string[] {
    this.version();
    const errors = this.form.get(path)?.errors ?? {};
    return Object.entries(errors).flatMap(([name, value]) =>
      name === 'server' ? (value as ServerControlError).messageKeys : [`local:${name}`],
    );
  }

  /** A failed submit: clear the old server errors, then bind the new ones. */
  applyServerError(error: AppError): void {
    clearServerErrors(this.form);
    this.unmapped.set(bindFieldErrors(this.form, error, { markTouched: true }));
  }

  /** How many edit bridges are wired; read by the spec. */
  get bridges(): number {
    return this.titleEdits().length + this.textEdits.length;
  }
}

describe('BindFieldErrors_PreservesLocalAndClearsServerLifecycle', () => {
  let fixture: ComponentFixture<StoryFormHostComponent>;
  let host: StoryFormHostComponent;
  let root: HTMLElement;

  const text = (selector: string): string[] =>
    Array.from(root.querySelectorAll(`${selector} li`)).map((item) => (item.textContent ?? '').trim());
  const type = (selector: string, value: string): void => {
    const input = root.querySelector<HTMLInputElement>(selector);
    if (input === null) throw new Error(`no ${selector}`);
    input.value = value;
    input.dispatchEvent(new Event('input'));
  };
  const submitDisabled = (): boolean => root.querySelector<HTMLButtonElement>('#submit')?.disabled ?? false;
  /** The server response arrives outside change detection, as an HTTP callback would. */
  const respond = async (error: AppError): Promise<void> => {
    await Promise.resolve();
    host.applyServerError(error);
    await fixture.whenStable();
  };

  beforeEach(async () => {
    activeBridges = 0;
    TestBed.configureTestingModule({ providers: [...ZONELESS_TEST_PROVIDERS] });
    fixture = TestBed.createComponent(StoryFormHostComponent);
    host = fixture.componentInstance;
    root = fixture.nativeElement as HTMLElement;
    await fixture.whenStable();
  });

  it('keeps local errors, shows server messages after whenStable, clears per edit and per resubmit, releases the bridge', async () => {
    expect(host.bridges).toBeGreaterThan(0);
    expect(text('#title-messages')).toEqual(['local:required']);

    // local validation: minlength
    type('#title', 'ab');
    await fixture.whenStable();
    expect(text('#title-messages')).toEqual(['local:minlength']);

    // first submit: the server names the title, a segment and a control that does not exist
    await respond(errorWith({ title: ['title_x'], 'segments.1.text': ['text_y'], ghost: ['g'] }));
    expect(text('#title-messages')).toEqual(['local:minlength', 'errors.validation_failed']);
    expect(text('#text-messages-1')).toEqual(['errors.validation_failed']);
    expect(text('#text-messages-0')).toEqual([]);
    expect(root.querySelector('#unmapped')?.textContent).toBe('ghost');
    expect(submitDisabled()).toBe(true);
    expect(host.form.controls.title.touched).toBe(true);

    // second submit replaces the server messages: only segment 2 is named now, and the unknown pointer changes
    await respond(errorWith({ 'segments.2.text': ['z'], other: ['o'] }, 'story_invalid_content'));
    expect(text('#title-messages')).toEqual(['local:minlength']);
    expect(text('#text-messages-1')).toEqual([]);
    expect(text('#text-messages-2')).toEqual(['errors.story_invalid_content']);
    expect(root.querySelector('#unmapped')?.textContent).toBe('other');

    // edit clears only the edited control's server namespace
    await respond(errorWith({ title: ['t'], 'segments.0.text': ['a'], 'segments.2.text': ['b'] }));
    expect(text('#title-messages')).toEqual(['local:minlength', 'errors.validation_failed']);
    type('#text-0', 'fixed');
    await fixture.whenStable();
    expect(text('#text-messages-0')).toEqual([]);
    expect(text('#text-messages-2')).toEqual(['errors.validation_failed']);
    expect(text('#title-messages')).toEqual(['local:minlength', 'errors.validation_failed']);

    // editing the title re-runs the local validators: minlength is recomputed, the server message is gone and stays gone
    type('#title', 'abc');
    await fixture.whenStable();
    expect(text('#title-messages')).toEqual([]);
    host.form.updateValueAndValidity();
    await fixture.whenStable();
    expect(text('#title-messages')).toEqual([]);
    expect(text('#text-messages-2')).toEqual(['errors.validation_failed']);
    expect(submitDisabled()).toBe(true); // segment 2 still has its server error

    // resubmit with an empty error clears every old server error; the submit button follows
    await respond(errorWith({}));
    expect(text('#text-messages-2')).toEqual([]);
    expect(root.querySelector('#unmapped')?.textContent).toBe('');
    expect(submitDisabled()).toBe(false);

    // destroy releases every bridge subscription
    expect(activeBridges).toBeGreaterThan(0);
    fixture.destroy();
    expect(activeBridges).toBe(0);
  });

  it('enable/disable and patchValue update the DOM after whenStable without forcing change detection', async () => {
    host.form.controls.segments.at(1).controls.text.disable();
    await fixture.whenStable();
    expect(root.querySelector<HTMLInputElement>('#text-1')?.disabled).toBe(true);
    host.form.controls.segments.at(1).controls.text.enable();
    host.form.patchValue({ title: 'hello' });
    await fixture.whenStable();
    expect(root.querySelector<HTMLInputElement>('#text-1')?.disabled).toBe(false);
    expect(root.querySelector<HTMLInputElement>('#title')?.value).toBe('hello');
    expect(submitDisabled()).toBe(false);
  });
});
