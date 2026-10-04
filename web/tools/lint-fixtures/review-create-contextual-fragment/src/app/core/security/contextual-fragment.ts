// Review F-3c: Range.createContextualFragment skips the Angular sanitizer.
export const parse = (range: Range, html: string): DocumentFragment => range.createContextualFragment(html);
