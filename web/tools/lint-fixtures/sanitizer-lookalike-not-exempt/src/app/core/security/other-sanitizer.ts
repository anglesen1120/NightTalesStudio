import { DomSanitizer } from '@angular/platform-browser';

// Same folder, same call, different file name: NOT exempt.
export function unreviewed(sanitizer: DomSanitizer, html: string): unknown {
  return sanitizer.bypassSecurityTrustHtml(html);
}
