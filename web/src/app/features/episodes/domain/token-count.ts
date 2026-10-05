/** Client hint only; the server response remains authoritative after save. */
export function countTokens(text: string): number {
  return text.normalize('NFC').split(/\s+/u).filter(Boolean).length;
}
