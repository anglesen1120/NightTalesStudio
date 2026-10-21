// QA defect: window.document.write(...) is still document.write (D2/D4); the selector needs callee.object.name='document'.
export const emit = (html: string): void => {
  window.document.write(html);
};
