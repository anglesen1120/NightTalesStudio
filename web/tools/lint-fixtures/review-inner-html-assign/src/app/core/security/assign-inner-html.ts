// Review F-3c: innerHTML assignment skips the Angular sanitizer.
export const write = (el: HTMLElement, html: string): void => {
  el.innerHTML = html;
};
