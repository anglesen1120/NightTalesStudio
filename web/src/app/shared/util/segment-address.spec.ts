import { addressForParagraph, compareAddresses, formatAddress, parseAddress, parsePointerAddress, remapAfterInsert, remapAfterRemove, remapAfterReorder } from './segment-address';
import type { SegmentAddress } from './segment-address';

describe('segment-address', () => {
  it.each(['title', 'hook', 'p:0', 'p:3', 'ending', 'meta'])('parseFormat_RoundTrip_%s', (address) => {
    const parsed = parseAddress(address);
    expect(parsed).not.toBeNull();
    if (parsed) expect(formatAddress(parsed.kind, parsed.index ?? undefined)).toBe(address);
  });
  it('paragraph_Zero_IsValid', () => { expect(addressForParagraph(0)).toBe('p:0'); expect(parseAddress('p:0')).toEqual({ kind: 'paragraph', index: 0 }); });
  it.each(['unknown', 'p:-1', 'p:01', 'p:1.2', 'p:Infinity', 'p:9007199254740992', 'p:', ' p:0', 'p:0.text', 'p:1\n'])('unknownAddress_ReturnsNull_%s', (value) => expect(parseAddress(value)).toBeNull());
  it.each([
    ['title', 'title'], ['p:0', 'p:0'], ['paragraphs[0]', 'p:0'], ['paragraphs[3].text', 'p:3'],
    ['paragraphs[2].keywords[1]', 'p:2'], ['paragraphs[1].fact_refs[0]', 'p:1'], ['paragraphs[2].beat', 'p:2'],
    ['hook.text', 'hook'], ['hook.keywords[0]', 'hook'], ['ending.fact_refs[1]', 'ending'],
    ['meta', 'meta'], ['description', 'meta'], ['tags[0]', 'meta'], ['content_warnings[1]', 'meta'],
  ])('parsePointerAddress_Maps_%s', (pointer, address) => expect(parsePointerAddress(pointer)).toBe(address));
  it.each([
    '', '__proto__', 'constructor', 'toString', 'HOOK', ' hook', 'hook.', 'hook.text\u0000', 'p:1\n', 'p:\uD800',
    'paragraphs[-1].text', 'paragraphs[01].text', 'paragraphs[1.5].text', 'paragraphs[9007199254740992].text',
    'paragraphs[0].text[1]', 'paragraphs[0].keywords[-1]', 'paragraphs[0].keywords[01]', 'paragraphs[0].secret',
    'tags[-1]', 'content_warnings[01]', 'structure.sub_genre', 'meta.__proto__', 'ending.text.other',
  ])('parsePointerAddress_RejectsHostileOrUnknown_%j', (pointer) => expect(parsePointerAddress(pointer)).toBeNull());
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
  it('remapAfterRemove_ShiftsLaterParagraphs_AndDropsTheRemovedOne', () => {
    expect(remapAfterRemove('p:1', 2)).toBe('p:1');
    expect(remapAfterRemove('p:2', 2)).toBeNull();
    expect(remapAfterRemove('p:3', 2)).toBe('p:2');
    expect(remapAfterRemove('hook', 2)).toBe('hook');
    expect(remapAfterRemove('meta', 0)).toBe('meta');
  });
  it('remapAfterRemove_InvalidIndex_Throws', () => {
    expect(() => remapAfterRemove('p:0', -1)).toThrow(RangeError);
  });
  it('remapAfterInsert_ShiftsFromTheInsertedIndexOnwards', () => {
    expect(remapAfterInsert('p:1', 2)).toBe('p:1');
    expect(remapAfterInsert('p:2', 2)).toBe('p:3');
    expect(remapAfterInsert('p:5', 2)).toBe('p:6');
    expect(remapAfterInsert('ending', 2)).toBe('ending');
  });
  it('remapAfterInsert_ThenRemapAfterRemove_IsTheIdentity', () => {
    for (const address of ['title', 'hook', 'p:0', 'p:2', 'p:5', 'ending', 'meta'] as const) {
      const shifted = remapAfterInsert(address, 2);
      expect(remapAfterRemove(shifted, 2)).toBe(address);
    }
  });
});
