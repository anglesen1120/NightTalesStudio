import { ChangeDetectionStrategy, Component, DestroyRef, ViewContainerRef, computed, effect, inject, signal, untracked } from '@angular/core';
import { takeUntilDestroyed, toSignal } from '@angular/core/rxjs-interop';
import { FormArray, FormControl, FormGroup, ReactiveFormsModule, Validators, ValueChangeEvent } from '@angular/forms';
import type { AbstractControl, ValidationErrors, ValidatorFn } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { TranslocoPipe, provideTranslocoScope } from '@jsverse/transloco';
import { Subject, debounceTime, firstValueFrom, takeUntil } from 'rxjs';
import { AppErrorMapper, bindFieldErrors, clearServerErrors } from '@core/errors';
import type { ServerControlError } from '@core/errors';
import { UnsavedWorkRegistry } from '@core/shell/unsaved-work';
import { CharCounter, ConfirmDialog, EmptyState, ErrorState, FormErrorAlert, LoadingState, PageHeader } from '@shared/ui';
import { BibleStore } from '../application/bible.store';
import { BIBLE_LIMITS } from '../domain/bible.model';
import type { BibleContent } from '../domain/bible.model';
import { BibleSection } from '../ui/bible-section';
import type { BibleEntryControls, BibleEntryForm, BibleSectionKind } from '../ui/bible-section';
import { BibleProposalBanner } from '../ui/bible-proposal-banner';

type NarratorControls = { name: FormControl<string>; persona: FormControl<string>; voiceNotes: FormControl<string> };
type BibleForm = FormGroup<{ narrator: FormGroup<NarratorControls>; recurringCharacters: FormArray<BibleEntryForm>; places: FormArray<BibleEntryForm>; rules: FormArray<BibleEntryForm>; episodeSummaries: FormArray<BibleEntryForm> }>;
interface FieldIssue { readonly pointer: string; readonly labelKey: string; readonly messageKey: string }
const noAngleBrackets: ValidatorFn = (control: AbstractControl): ValidationErrors | null => typeof control.value === 'string' && /[<>]/.test(control.value) ? { angleBrackets: true } : null;

/** Compare JSON content independent of object-key order; omitted and undefined-valued keys are equivalent. */
function sameContent(left: unknown, right: unknown): boolean {
  if (left === right) return true;
  if (left === null || right === null || typeof left !== 'object' || typeof right !== 'object' || Array.isArray(left) !== Array.isArray(right)) return false;
  const before = left as Record<string, unknown>;
  const after = right as Record<string, unknown>;
  const keys = Object.keys(before).filter(key => before[key] !== undefined);
  return keys.length === Object.keys(after).filter(key => after[key] !== undefined).length && keys.every(key => Object.hasOwn(after, key) && sameContent(before[key], after[key]));
}

@Component({
  selector: 'nts-bible-page',
  imports: [ReactiveFormsModule, RouterLink, TranslocoPipe, MatButtonModule, MatFormFieldModule, MatInputModule, PageHeader, LoadingState, ErrorState, EmptyState, FormErrorAlert, CharCounter, BibleSection, BibleProposalBanner],
  providers: [BibleStore, provideTranslocoScope('series', 'errors')],
  templateUrl: './bible-page.html', styleUrl: './bible-page.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { '(window:beforeunload)': 'beforeUnload($event)' },
})
export class BiblePage {
  readonly store = inject(BibleStore);
  private readonly route = inject(ActivatedRoute);
  private readonly params = toSignal(this.route.paramMap, { initialValue: this.route.snapshot.paramMap });
  readonly seriesId = computed(() => this.params().get('id') ?? '');
  private readonly destroy = inject(DestroyRef);
  private readonly dialog = inject(ConfirmDialog);
  private readonly viewContainer = inject(ViewContainerRef);
  private readonly mapper = inject(AppErrorMapper);
  private readonly unsaved = inject(UnsavedWorkRegistry);
  readonly form = signal<BibleForm | null>(null);
  readonly dirty = signal(false);
  readonly saved = signal(false);
  readonly revision = signal(0);
  readonly unmappedKeys = signal<readonly string[]>([]);
  readonly fieldIssues = signal<readonly FieldIssue[]>([]);
  private baseline = '';
  private adoptedContent: BibleContent | null = null;
  private leavePrompt: Promise<boolean> | null = null;
  private accepting = false;
  private generation = 0;
  readonly narratorFields = [
    { name: 'name', labelKey: 'series.bible.narrator.name', max: BIBLE_LIMITS.narratorName },
    { name: 'persona', labelKey: 'series.bible.narrator.persona', max: BIBLE_LIMITS.narratorPersona },
    { name: 'voiceNotes', labelKey: 'series.bible.narrator.voiceNotes', max: BIBLE_LIMITS.narratorVoiceNotes },
  ] as const;
  readonly canSave = computed(() => {
    this.revision();
    return this.dirty() && this.form()?.valid === true && !this.store.saving() && !this.store.committing() && !this.store.loading() && !this.store.conflict();
  });
  readonly errorKey = computed(() => {
    const error = this.store.error();
    return error ? this.mapper.messageKey(error) : 'errors.unknown_error';
  });
  readonly formError = computed(() => {
    const error = this.store.error();
    return this.store.conflict() || error?.code === 'bible_proposal_not_pending' ? null : error;
  });
  readonly isEmpty = computed(() => {
    const content = this.store.bible()?.content;
    return content !== undefined && !content.narrator.name && !content.narrator.persona && !content.narrator.voiceNotes && !content.recurringCharacters.length && !content.places.length && !content.rules.length && !content.episodeSummaries.length;
  });
  constructor() {
    effect(() => {
      const id = this.seriesId();
      untracked(() => { this.discard(); if (id) void this.store.load(id); });
    });
    effect(() => {
      const bible = this.store.bible();
      if (this.store.loading() || this.store.saving() || this.store.committing()) return;
      // A refetch maps a fresh object even when Dismiss/409 did not change the server content.
      if (bible && !sameContent(bible.content, this.adoptedContent)) untracked(() => this.buildForm(bible.content));
    });
    effect(onCleanup => {
      const form = this.form();
      if (!form) return;
      const stopped = new Subject<void>();
      void form.events.pipe(takeUntil(stopped), takeUntilDestroyed(this.destroy)).forEach(event => {
        if (event instanceof ValueChangeEvent && event.source instanceof FormControl) clearServerErrors(event.source);
        this.revision.update(value => value + 1);
      });
      void form.valueChanges.pipe(debounceTime(150), takeUntil(stopped), takeUntilDestroyed(this.destroy)).forEach(() => {
        const changed = JSON.stringify(form.getRawValue()) !== this.baseline;
        this.dirty.set(changed);
        this.saved.set(false);
        if (!changed) form.markAsPristine();
      });
      onCleanup(() => { stopped.next(); stopped.complete(); });
    });
    effect(() => {
      const error = this.store.error();
      const form = this.form();
      if (!form || error?.code !== 'bible_invalid_content') return;
      untracked(() => {
        const normalised: Record<string, readonly string[]> = {};
        const original = new Map<string, string>();
        for (const [pointer, messages] of Object.entries(error.fieldErrors)) {
          const canonical = pointer.replace(/^content\./, '').replace(/\[(\d+)\]/g, '.$1').replace(/^recurring_characters(?=\.|$)/, 'recurringCharacters').replace(/^episode_summaries(?=\.|$)/, 'episodeSummaries').replace(/\.voice_notes$/, '.voiceNotes').replace(/\.episode_id$/, '.episodeId').replace(/\.first_episode_id$/, '.firstEpisodeId');
          normalised[canonical] = messages;
          original.set(canonical, pointer);
        }
        const missing = bindFieldErrors(form, { ...error, fieldErrors: normalised }, { markTouched: true });
        this.unmappedKeys.set(missing.map(pointer => original.get(pointer) ?? pointer));
        this.fieldIssues.set(Object.entries(normalised).map(([pointer, messages]) => {
          const last = pointer.split('.').at(-1);
          const labelKey = pointer.startsWith('narrator.') && (last === 'name' || last === 'persona' || last === 'voiceNotes') ? `series.bible.narrator.${last}` : pointer.startsWith('recurringCharacters') ? 'series.bible.characters' : pointer.startsWith('places') ? 'series.bible.places' : pointer.startsWith('rules') ? 'series.bible.rules' : pointer.startsWith('episodeSummaries') ? 'series.bible.summaries' : 'series.bible.title';
          const code = messages[0];
          const messageKey = code === 'too_many' ? 'series.bible.field.too_many' : code === 'too_long' ? 'series.bible.field.too_long' : code === 'duplicate' ? 'series.bible.field.duplicate' : code === 'unknown_episode' || code === 'not_approved_episode' ? 'series.bible.field.unknown_episode' : 'series.bible.field.invalid';
          const control = form.get(pointer.split('.'));
          if (control) control.setErrors({ ...control.errors, server: { messageKeys: [messageKey] } satisfies ServerControlError });
          return { pointer, labelKey, messageKey };
        }));
        this.revision.update(value => value + 1);
      });
    });
    this.destroy.onDestroy(this.unsaved.register({ hasUnsaved: () => this.hasUnsaved(), discard: () => this.discard(), confirmLeave: () => this.confirmLeave() }));
    this.destroy.onDestroy(() => this.store.reset());
  }
  private buildForm(content: BibleContent): void {
    const text = (value: string, max: number, name = false): FormControl<string> => new FormControl(value, { nonNullable: true, validators: name ? [Validators.maxLength(max), noAngleBrackets] : [Validators.maxLength(max)] });
    const group = (controls: BibleEntryControls): BibleEntryForm => new FormGroup<BibleEntryControls>(controls);
    const persona = text(content.narrator.persona, BIBLE_LIMITS.narratorPersona);
    persona.addValidators(control => typeof control.value === 'string' && control.value.trim().length > 0 ? null : { required: true }); persona.updateValueAndValidity();
    const form: BibleForm = new FormGroup({
      narrator: new FormGroup({ name: text(content.narrator.name, BIBLE_LIMITS.narratorName, true), persona, voiceNotes: text(content.narrator.voiceNotes, BIBLE_LIMITS.narratorVoiceNotes) }),
      recurringCharacters: new FormArray(content.recurringCharacters.map(row => group({ id: text(row.id ?? '', 12), name: text(row.name, BIBLE_LIMITS.characterName, true), description: text(row.description, BIBLE_LIMITS.description), firstEpisodeId: text(row.firstEpisodeId ?? '', 36) }))),
      places: new FormArray(content.places.map(row => group({ id: text(row.id ?? '', 12), name: text(row.name, BIBLE_LIMITS.placeName, true), description: text(row.description, BIBLE_LIMITS.description) }))),
      rules: new FormArray(content.rules.map(row => group({ id: text(row.id ?? '', 12), text: text(row.text, BIBLE_LIMITS.ruleText) }))),
      episodeSummaries: new FormArray(content.episodeSummaries.map(row => group({ episodeId: text(row.episodeId, 36), title: text(row.title, BIBLE_LIMITS.summaryTitle), summary: text(row.summary, BIBLE_LIMITS.summaryBody) }))),
    });
    this.adoptedContent = content;
    this.baseline = JSON.stringify(form.getRawValue());
    this.dirty.set(false); this.fieldIssues.set([]); this.unmappedKeys.set([]); this.form.set(form);
  }
  addEntry(kind: Exclude<BibleSectionKind, 'summaries'>): void {
    const form = this.form();
    if (!form || this.store.saving() || this.store.committing() || this.store.loading()) return;
    const rows = kind === 'characters' ? form.controls.recurringCharacters : kind === 'places' ? form.controls.places : form.controls.rules;
    const max = kind === 'characters' ? BIBLE_LIMITS.maxCharacters : kind === 'places' ? BIBLE_LIMITS.maxPlaces : BIBLE_LIMITS.maxRules;
    if (rows.length >= max) return;
    clearServerErrors(rows);
    const control = (max: number, name = false): FormControl<string> => new FormControl('', { nonNullable: true, validators: name ? [Validators.maxLength(max), noAngleBrackets] : [Validators.maxLength(max)] });
    rows.push(new FormGroup<BibleEntryControls>(kind === 'rules' ? { text: control(BIBLE_LIMITS.ruleText) } : { name: control(kind === 'characters' ? BIBLE_LIMITS.characterName : BIBLE_LIMITS.placeName, true), description: control(BIBLE_LIMITS.description) }));
    form.markAsDirty();
  }
  removeEntry(kind: BibleSectionKind, index: number): void {
    const form = this.form();
    if (!form || this.store.saving() || this.store.committing() || this.store.loading()) return;
    const rows = kind === 'characters' ? form.controls.recurringCharacters : kind === 'places' ? form.controls.places : kind === 'rules' ? form.controls.rules : form.controls.episodeSummaries;
    clearServerErrors(rows); rows.removeAt(index); form.markAsDirty();
  }
  async save(): Promise<void> {
    const form = this.form(); const bible = this.store.bible();
    if (!form || !bible || !this.canSave()) return;
    clearServerErrors(form); this.fieldIssues.set([]); this.unmappedKeys.set([]);
    const value = form.getRawValue();
    const content: BibleContent = {
      narrator: value.narrator,
      recurringCharacters: value.recurringCharacters.map(row => ({ ...(row.id ? { id: row.id } : {}), name: row.name ?? '', description: row.description ?? '', ...(row.firstEpisodeId ? { firstEpisodeId: row.firstEpisodeId } : {}) })),
      places: value.places.map(row => ({ ...(row.id ? { id: row.id } : {}), name: row.name ?? '', description: row.description ?? '' })),
      rules: value.rules.map(row => ({ ...(row.id ? { id: row.id } : {}), text: row.text ?? '' })),
      episodeSummaries: value.episodeSummaries.map(row => ({ episodeId: row.episodeId ?? '', title: row.title ?? '', summary: row.summary ?? '' })),
    };
    const id = this.seriesId();
    const generation = this.generation;
    const result = await this.store.save(id, content, bible.etag);
    if (result.ok && !this.destroy.destroyed && generation === this.generation && id === this.seriesId()) {
      const savedBible = this.store.bible();
      if (savedBible && this.form() === form) this.buildForm(savedBible.content);
      this.saved.set(true);
    }
  }
  async reload(): Promise<void> {
    if (this.store.loading() || this.store.saving() || this.store.committing()) return;
    const id = this.seriesId();
    const generation = this.generation;
    const editor = this.form();
    await this.store.load(id);
    const bible = this.store.bible();
    if (bible && this.form() === editor && !this.store.error() && !this.destroy.destroyed && generation === this.generation && id === this.seriesId()) this.buildForm(bible.content);
  }
  async acceptProposal(): Promise<void> {
    if (this.accepting || this.store.loading() || this.store.saving() || this.store.committing()) return;
    const id = this.seriesId();
    const generation = this.generation;
    const proposal = this.store.proposal();
    if (!proposal) return;
    this.accepting = true;
    try {
      const form = this.form();
      if (this.hasUnsaved() || (form && JSON.stringify(form.getRawValue()) !== this.baseline)) {
        const ref = this.dialog.confirm({ titleKey: 'series.bible.dirty.acceptTitle', bodyKey: 'series.bible.dirty.body', confirmLabelKey: 'series.bible.dirty.leave', cancelLabelKey: 'series.bible.dirty.stay', destructive: true }, { viewContainerRef: this.viewContainer });
        if (!(await firstValueFrom(ref.afterClosed()))) return;
      }
      if (this.destroy.destroyed || generation !== this.generation || id !== this.seriesId() || proposal !== this.store.proposal() || this.store.loading() || this.store.saving() || this.store.committing()) return;
      // Keep the draft until content actually changes: a 409/refetch must not discard it.
      await this.store.acceptProposal(id);
    } finally {
      this.accepting = false;
    }
  }
  async dismissProposal(): Promise<void> { if (!this.store.saving() && !this.store.committing()) await this.store.dismissProposal(this.seriesId()); }
  controlErrorKey(control: FormControl<string>): string {
    const server = control.getError('server') as ServerControlError | undefined;
    return server?.messageKeys[0] ?? (control.hasError('required') ? 'series.bible.field.required' : control.hasError('angleBrackets') ? 'series.bible.field.invalidName' : 'series.bible.field.too_long');
  }
  private hasUnsaved(): boolean { return this.dirty() || this.form()?.dirty === true || this.store.saving(); }
  beforeUnload(event: BeforeUnloadEvent): void { if (this.hasUnsaved()) { event.preventDefault(); event.returnValue = ''; } }
  confirmLeave(): Promise<boolean> {
    if (!this.hasUnsaved()) return Promise.resolve(true);
    this.leavePrompt ??= this.askLeave().finally(() => { this.leavePrompt = null; });
    return this.leavePrompt;
  }
  private async askLeave(): Promise<boolean> {
    const ref = this.dialog.confirm({ titleKey: 'series.bible.dirty.title', bodyKey: 'series.bible.dirty.body', confirmLabelKey: 'series.bible.dirty.leave', cancelLabelKey: 'series.bible.dirty.stay', destructive: true, pending: this.store.saving }, { viewContainerRef: this.viewContainer });
    const leave = (await firstValueFrom(ref.afterClosed())) ?? false;
    if (leave && !this.destroy.destroyed) this.discard();
    return leave;
  }
  private discard(): void {
    this.generation++;
    this.form.set(null); this.dirty.set(false); this.saved.set(false); this.adoptedContent = null; this.fieldIssues.set([]); this.unmappedKeys.set([]); this.store.reset();
  }
}
