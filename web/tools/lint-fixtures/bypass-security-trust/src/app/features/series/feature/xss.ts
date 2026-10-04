import { DomSanitizer } from '@angular/platform-browser';

export function unsafe(sanitizer: DomSanitizer, url: string): unknown {
  return sanitizer.bypassSecurityTrustResourceUrl(url);
}
