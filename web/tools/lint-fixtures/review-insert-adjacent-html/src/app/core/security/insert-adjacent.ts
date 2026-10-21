// Review F-3c: insertAdjacentHTML skips the Angular sanitizer.
export const write = (el: HTMLElement, html: string): void => {
  el.insertAdjacentHTML('beforeend', html);
};
