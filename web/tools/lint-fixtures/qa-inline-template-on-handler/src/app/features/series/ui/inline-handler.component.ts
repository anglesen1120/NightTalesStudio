// QA defect: an inline on*= handler in an inline template is not scanned (the CSP generator fails on it later, D4).
import { ChangeDetectionStrategy, Component } from '@angular/core';

@Component({
  selector: 'app-inline-handler',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `<button type="button" onclick="go()">Go</button>`,
})
export class InlineHandler {}
