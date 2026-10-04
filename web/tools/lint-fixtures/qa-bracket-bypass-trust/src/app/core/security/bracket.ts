// QA defect: bypassSecurityTrustHtml through a string-literal computed member (D2/D4).
import { DomSanitizer } from '@angular/platform-browser';

export const trust = (sanitizer: DomSanitizer, html: string) => sanitizer['bypassSecurityTrustHtml'](html);
