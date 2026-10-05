export type SegmentAddress = 'title' | 'hook' | `p:${number}` | 'ending' | 'meta';
export type SegmentKind = 'title' | 'hook' | 'paragraph' | 'ending' | 'meta';
export interface ParsedAddress { readonly kind: SegmentKind; readonly index: number | null }

/** Canonical, zero-based addresses only; unknown values never select a segment. */
export function parseAddress(address: string): ParsedAddress | null {
  if (address === 'title' || address === 'hook' || address === 'ending' || address === 'meta') return { kind: address, index: null };
  if (!/^p:(0|[1-9]\d*)$/.test(address)) return null;
  const index = Number(address.substring(2));
  return Number.isSafeInteger(index) ? { kind: 'paragraph', index } : null;
}

export function addressForParagraph(index: number): SegmentAddress {
  if (!Number.isSafeInteger(index) || index < 0) throw new RangeError('Invalid paragraph index');
  return `p:${index}`;
}

export function formatAddress(kind: SegmentKind, index?: number): SegmentAddress {
  if (kind === 'paragraph') return addressForParagraph(index ?? -1);
  return kind;
}

export function compareAddresses(a: SegmentAddress, b: SegmentAddress): number {
  const rank = (address: SegmentAddress): readonly [number, number] => {
    const parsed = parseAddress(address);
    switch (parsed?.kind) {
      case 'title': return [0, 0];
      case 'hook': return [1, 0];
      case 'paragraph': return [2, parsed.index ?? 0];
      case 'ending': return [3, 0];
      case 'meta': return [4, 0];
      default: throw new RangeError('Invalid segment address');
    }
  };
  const left = rank(a), right = rank(b);
  return left[0] - right[0] || left[1] - right[1];
}

export function remapAfterReorder(addresses: readonly SegmentAddress[], movedFrom: number, movedTo: number): SegmentAddress[] {
  addressForParagraph(movedFrom);
  addressForParagraph(movedTo);
  return addresses.map((address) => {
    const parsed = parseAddress(address);
    if (parsed?.kind !== 'paragraph' || parsed.index === null) return address;
    const index = parsed.index;
    if (index === movedFrom) return addressForParagraph(movedTo);
    if (movedFrom < movedTo && index > movedFrom && index <= movedTo) return addressForParagraph(index - 1);
    if (movedFrom > movedTo && index >= movedTo && index < movedFrom) return addressForParagraph(index + 1);
    return address;
  });
}
