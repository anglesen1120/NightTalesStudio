import { addressForParagraph, compareAddresses, formatAddress, parseAddress, remapAfterReorder } from './segment-address';
import type { SegmentAddress } from './segment-address';

describe('segment-address', () => {
  it.each(['title', 'hook', 'p:0', 'p:3', 'ending', 'meta'])('parseFormat_RoundTrip_%s', (address) => {
    const parsed = parseAddress(address);
    expect(parsed).not.toBeNull();
    if (parsed) expect(formatAddress(parsed.kind, parsed.index ?? undefined)).toBe(address);
  });
  it('paragraph_Zero_IsValid', () => { expect(addressForParagraph(0)).toBe('p:0'); expect(parseAddress('p:0')).toEqual({ kind: 'paragraph', index: 0 }); });
  it.each(['unknown', 'p:-1', 'p:01', 'p:1.2', 'p:Infinity', 'p:9007199254740992', 'p:', ' p:0', 'p:0.text'])('unknownAddress_ReturnsNull_%s', (value) => expect(parseAddress(value)).toBeNull());
  it('compareAddresses_UsesDocumentOrder', () => {
    const values: SegmentAddress[] = ['meta', 'p:1', 'ending', 'title', 'p:0', 'hook'];
    expect(values.sort(compareAddresses)).toEqual(['title', 'hook', 'p:0', 'p:1', 'ending', 'meta']);
  });
  it('remapAfterReorder_MovesIdentityAndLeavesUnrelatedAddressesStable', () => {
    const values: readonly SegmentAddress[] = ['title', 'hook', 'p:0', 'p:1', 'p:2', 'p:3', 'ending', 'meta'];
    expect(remapAfterReorder(values, 1, 3)).toEqual(['title', 'hook', 'p:0', 'p:3', 'p:1', 'p:2', 'ending', 'meta']);
    expect(remapAfterReorder(remapAfterReorder(values, 1, 3), 3, 1)).toEqual(values);
    expect(remapAfterReorder(values, 2, 2)).toEqual(values);
  });
  it.each([-1, 0.5, Infinity, Number.MAX_SAFE_INTEGER + 1])('invalidParagraphIndex_Throws_%s', (index) => expect(() => addressForParagraph(index)).toThrow(RangeError));
});
