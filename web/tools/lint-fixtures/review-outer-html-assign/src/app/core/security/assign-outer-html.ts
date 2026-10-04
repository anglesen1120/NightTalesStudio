// Review F-3c: outerHTML assignment skips the Angular sanitizer.
export const write = (el: HTMLElement, html: string): void => {
  el.outerHTML = html;
};
