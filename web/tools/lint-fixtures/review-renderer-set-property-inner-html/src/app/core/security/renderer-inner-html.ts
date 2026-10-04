// Review F-3c: Renderer2.setProperty of innerHTML skips the Angular sanitizer.
import { Renderer2 } from '@angular/core';

export const write = (renderer: Renderer2, el: HTMLElement, html: string): void => {
  renderer.setProperty(el, 'innerHTML', html);
};
