export type SegmentAddress = 'title' | 'hook' | `p:${number}` | 'ending' | 'meta';
export type SegmentKind = 'title' | 'hook' | 'paragraph' | 'ending' | 'meta';
export interface ParsedAddress { readonly kind: SegmentKind; readonly index: number | null }

/** Canonical, zero-based addresses only; unknown values never select a segment. */
export function parseAddress(address: string): ParsedAddress | null {
  if (address === 'title' || address === 'hook' || address === 'ending' || address === 'meta') return { kind: address, index: null };
  const match = /^p:(0|[1-9]\d*)$/.exec(address);
  if (!match || match[0] !== address) return null;
  const index = Number(match[1]);
  return Number.isSafeInteger(index) ? { kind: 'paragraph', index } : null;
}

/** Map validated story.v1 field pointers to canonical editor addresses. */
export function parsePointerAddress(pointer: string): SegmentAddress | null {
  if (/[\u0000-\u0020\u007f-\u009f]/u.test(pointer)) return null;
  const canonical = parseAddress(pointer);
  if (canonical !== null) return formatAddress(canonical.kind, canonical.index ?? undefined);
  const paragraph = /^paragraphs\[(0|[1-9]\d*)\](?:\.(?:text|beat|(?:keywords|fact_refs)(?:\[(0|[1-9]\d*)\])?))?$/.exec(pointer);
  if (paragraph !== null) {
    const address = parseAddress(`p:${paragraph[1]}`);
    return address?.kind === 'paragraph' ? addressForParagraph(address.index ?? -1) : null;
  }
  const segment = /^(hook|ending)(?:\.(?:text|(?:keywords|fact_refs)(?:\[(0|[1-9]\d*)\])?))?$/.exec(pointer);
  if (segment?.[1] === 'hook' || segment?.[1] === 'ending') return segment[1];
  if (/^(?:description|(?:tags|content_warnings)(?:\[(0|[1-9]\d*)\])?)$/.test(pointer)) return 'meta';
  return null;
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
