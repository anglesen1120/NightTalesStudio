import { DomSanitizer } from '@angular/platform-browser';

// The one exempt file (10.1.6-T1, CR-2): bypassSecurityTrust* is allowed here and nowhere else.
export function reviewed(sanitizer: DomSanitizer, html: string): unknown {
  return sanitizer.bypassSecurityTrustHtml(html);
}
