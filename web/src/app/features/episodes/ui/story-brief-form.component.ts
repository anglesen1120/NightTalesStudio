import { ChangeDetectionStrategy, Component, input, output } from '@angular/core';
import { toObservable, toSignal } from '@angular/core/rxjs-interop';
import { ReactiveFormsModule, type FormControl, type FormGroup } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { TranslocoPipe, provideTranslocoScope } from '@jsverse/transloco';
import { scan, switchMap } from 'rxjs';
import { CharCounter, RetryCountdown } from '@shared/ui';
import type { LengthTier } from '../domain/episode.model';

export interface StoryBriefFormControls {
  title: FormControl<string | null>;
  brief: FormControl<string>;
  lengthTier: FormControl<LengthTier>;
}

let nextBriefFormId = 0;

/** Presentational form; the route-scoped store owns its controls and request state. */
@Component({
  selector: 'nts-story-brief-form',
  imports: [ReactiveFormsModule, RouterLink, TranslocoPipe, CharCounter, RetryCountdown],
  providers: [provideTranslocoScope('episodes', 'story', 'errors')],
  templateUrl: './story-brief-form.component.html',
  styleUrl: './story-brief-form.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class StoryBriefFormComponent {
  readonly form = input.required<FormGroup<StoryBriefFormControls>>();
  readonly briefLength = input.required<number>();
  readonly isTrueCrime = input(false);
  readonly submitting = input(false);
  readonly canSubmit = input(false);
  readonly retryAfterSec = input<number | null>(null);
  readonly unmappedFieldKeys = input<readonly string[]>([]);
  readonly errorKey = input<string | null>(null);
  readonly traceId = input<string | null>(null);
  readonly createLost = input(false);
  readonly gateBlocked = input(false);
  readonly episodeId = input<string | null>(null);

  readonly submitted = output<void>();
  readonly countdownElapsed = output<void>();

  protected readonly id = `nts-story-brief-${nextBriefFormId++}`;
  protected readonly tiers = ['short', 'standard', 'long'] as const;
  private readonly formRevision = toSignal(
    toObservable(this.form).pipe(switchMap((form) => form.events), scan((revision) => revision + 1, 0)),
    { initialValue: 0 },
  );

  protected serverKeys(field: keyof StoryBriefFormControls): readonly string[] {
    this.formRevision();
    const server = this.form().controls[field].getError('server') as
      | { readonly messageKeys: readonly string[] }
      | undefined;
    return server?.messageKeys ?? [];
  }

  protected localErrorKey(field: keyof StoryBriefFormControls): string | null {
    this.formRevision();
    const control = this.form().controls[field];
    if (field === 'title' && control.hasError('maxlength')) return 'episodes.new.validation.titleMax';
    if (field === 'brief') {
      if (control.hasError('maxlength')) return 'episodes.new.validation.briefMax';
      if (control.hasError('required') || this.form().controls.brief.value.trim().length === 0)
        return 'episodes.new.validation.briefRequired';
    }
    return field === 'lengthTier' && control.invalid && this.serverKeys(field).length === 0
      ? 'errors.validation_failed'
      : null;
  }

  protected invalid(field: keyof StoryBriefFormControls): boolean {
    return this.localErrorKey(field) !== null || this.serverKeys(field).length > 0;
  }

  protected describedBy(field: keyof StoryBriefFormControls): string | null {
    const ids: string[] = [];
    if (field === 'brief') ids.push(`${this.id}-brief-count`, `${this.id}-brief-hint`);
    if (this.invalid(field)) ids.push(`${this.id}-${field}-error`);
    return ids.length === 0 ? null : ids.join(' ');
  }

  protected submit(): void {
    // Invalid Enter/programmatic submits still reach the page's client-validation focus path.
    if (this.submitting() || this.gateBlocked() || this.retryAfterSec() !== null) return;
    if (!this.canSubmit() && this.form().valid && this.form().controls.brief.value.trim().length > 0) return;
    this.submitted.emit();
  }
}
