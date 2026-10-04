// QA defect: document['write'](...) (string-literal computed member) is still document.write (D2/D4).
export const emit = (html: string): void => {
  document['write'](html);
};
