import { ChangeDetectionStrategy, Component, ElementRef, Renderer2, afterRenderEffect, computed, inject, input, output, viewChild } from '@angular/core';
import { MatCheckboxModule } from '@angular/material/checkbox';
import { TranslocoPipe } from '@jsverse/transloco';
import type { Attestation } from '../../domain/story.model';

let nextAttestationId = 0;

@Component({
  selector: 'nts-attestation-block',
  imports: [MatCheckboxModule, TranslocoPipe],
  templateUrl: './attestation-block.component.html',
  styleUrl: './attestation-block.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class AttestationBlockComponent {
  readonly attestation = input.required<Attestation>();
  readonly accepted = input(false);
  readonly invalid = input(false);
  readonly acceptedChange = output<boolean>();

  protected readonly errorId = `nts-attestation-error-${++nextAttestationId}`;
  protected readonly paragraphs = computed(() => this.attestation().text?.split(/\n\s*\n/).filter((paragraph) => paragraph.trim().length > 0) ?? []);

  private readonly checkbox = viewChild<unknown, ElementRef<HTMLElement>>('checkbox', { read: ElementRef });
  private readonly renderer = inject(Renderer2);

  constructor() {
    // Material forwards aria-describedby but has no aria-invalid input. Set it on the native control.
    afterRenderEffect(() => {
      const invalid = this.invalid();
      const control = this.checkbox()?.nativeElement.querySelector('input[type="checkbox"]');
      if (control) this.renderer.setAttribute(control, 'aria-invalid', String(invalid));
    });
  }
}
