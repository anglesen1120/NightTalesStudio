import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import { TranslocoPipe } from '@jsverse/transloco';
import { IconCheck } from '../icons/icon-check/icon-check';

export interface StepperStep {
  readonly labelKey: string;
  readonly state: 'done' | 'current' | 'todo';
}

@Component({
  selector: 'nts-stepper',
  imports: [TranslocoPipe, IconCheck],
  templateUrl: './stepper.html',
  styleUrl: './stepper.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { '[class.stepper--vertical]': "orientation() === 'vertical'" },
})
export class Stepper {
  readonly steps = input.required<readonly StepperStep[]>();
  readonly orientation = input<'horizontal' | 'vertical'>('horizontal');
  protected readonly labelledSteps = computed(() => this.steps().map((step, index) => ({ step, number: index + 1 })));
}
