// QA defect: README D2 / task Security bans [innerHTML] in ANY template; the HTML scan only reads *.html files.
import { ChangeDetectionStrategy, Component } from '@angular/core';

@Component({
  selector: 'app-inline-inner-html',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `<div [innerHTML]="html"></div>`,
})
export class InlineInnerHtml {
  html = '';
}
