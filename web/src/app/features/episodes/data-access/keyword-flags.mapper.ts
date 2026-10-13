import { formatAddress, parseAddress } from '@shared/util/segment-address';
import type { KeywordFlag } from '../domain/annotation.model';

/**
 * `409 keywords_flagged` `errors[]` rows → validated chips (10.5.10-T1, `03.2.2-T2`).
 *
 * A malformed row is dropped rather than failing the whole response, so one bad entry never hides the others; the
 * list is capped and deduped. A rule name this client does not know is still kept (the UI falls back to the generic
 * copy) as long as it is well shaped.
 */

/** Rule names are backend identifiers; a bounded `[A-Za-z0-9_.:-]` shape keeps a hostile string out of the DOM id. */
const RULE_PATTERN = /^[A-Za-z0-9_.:-]{1,64}$/;
const MAX_FLAGS = 100;
/** `StorySegmentWire.keywords` holds 1–2 chips today; anything past the tenth is not a real index. */
const MAX_KEYWORD_INDEX = 9;

export function mapKeywordsFlagged(errors: unknown): readonly KeywordFlag[] {
  if (!Array.isArray(errors)) return [];
  const flags: KeywordFlag[] = [];
  const seen = new Set<string>();
  for (const row of errors) {
    if (flags.length >= MAX_FLAGS) break;
    if (typeof row !== 'object' || row === null || Array.isArray(row)) continue;
    const source = row as Readonly<Record<string, unknown>>;
    const segment = source['segment'];
    const index = source['index'];
    const rule = source['rule'];
    if (typeof segment !== 'string' || typeof rule !== 'string' || !RULE_PATTERN.test(rule)) continue;
    const parsed = parseAddress(segment);
    if (parsed === null) continue;
    if (typeof index !== 'number' || !Number.isInteger(index) || index < 0 || index > MAX_KEYWORD_INDEX) continue;
    const address = formatAddress(parsed.kind, parsed.index ?? undefined);
    const identity = `${address}|${String(index)}|${rule}`;
    if (seen.has(identity)) continue;
    seen.add(identity);
    flags.push({ address, index, rule });
  }
  return flags;
}
