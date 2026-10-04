// QA defect: bypassSecurityTrustHtml through destructuring is not a MemberExpression (D2/D4).
import { DomSanitizer } from '@angular/platform-browser';

export const trust = (sanitizer: DomSanitizer, html: string) => {
  const { bypassSecurityTrustHtml } = sanitizer;
  return bypassSecurityTrustHtml(html);
};
