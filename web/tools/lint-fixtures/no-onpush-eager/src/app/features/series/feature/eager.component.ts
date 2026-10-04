import { ChangeDetectionStrategy, Component } from '@angular/core';

@Component({
  selector: 'app-eager',
  template: '',
  changeDetection: ChangeDetectionStrategy.Eager,
})
export class EagerComponent {}
