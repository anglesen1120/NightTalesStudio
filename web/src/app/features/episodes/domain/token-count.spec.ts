import { countTokens } from './token-count';
describe('countTokens', () => {
  it('NfcCompositionAndDecomposition_CountEqually', () => expect(countTokens('Tiếng Việt'.normalize('NFD'))).toBe(countTokens('Tiếng Việt')));
  it('VietnameseDiacritics_CountAsOneWord', () => expect(countTokens('đường')).toBe(1));
  it('SpacesTabsAndNewlines_AreUnicodeWhitespace', () => expect(countTokens('  một\t hai\n ba\u00a0bốn\u2003năm  ')).toBe(5));
  it.each(['', ' ', '\t\n'])('emptyText_%s_HasZeroTokens', (text) => expect(countTokens(text)).toBe(0));
});
