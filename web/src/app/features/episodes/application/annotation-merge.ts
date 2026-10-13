import { addressForParagraph, compareAddresses } from '@shared/util/segment-address';
import type { SegmentAddress } from '@shared/util/segment-address';
import type { KeywordFlag, SegmentAnnotation } from '../domain/annotation.model';
import type { QualityHint, StoryDraft, StorySegment } from '../domain/story.model';

/**
 * Pure, deterministic annotation merge (10.5.10-T1). Input is the **saved** hint set and the `409 keywords_flagged`
 * rows, never the draft text, so an edit cannot re-key or re-sort the feedback under the user's cursor.
 *
 * Review issues are deliberately not an input: they already arrive flattened inside `qualityHints` (`02.3.4-T2:42`),
 * and merging them again would render every one twice — hence there is no `review` source.
 */

/** The key shape D10 allows through verbatim; anything else is replaced by the shared "not translated" copy. */
const MESSAGE_KEY_PATTERN = /^[A-Za-z0-9._:-]+$/;
const UNKNOWN_MESSAGE_KEY = 'episodes.annotations.unknown';
const KEYWORD_FALLBACK_MESSAGE_KEY = 'episodes.editor.keywordFlaggedHint';
/** The three wire rule names of `03.2.2-T2`; any other well-shaped rule keeps the generic keyword copy. */
const KEYWORD_RULE_MESSAGE_KEYS: Readonly<Record<string, string>> = {
  keyword_name_detected: 'errors.keywordsFlaggedRule.keyword_name_detected',
  keyword_invalid: 'errors.keywordsFlaggedRule.keyword_invalid',
  no_scenes: 'errors.keywordsFlaggedRule.no_scenes',
};
const KIND_ORDER: Readonly<Record<SegmentAnnotation['kind'], number>> = { objective: 0, taste: 1 };
const SEVERITY_ORDER: Readonly<Record<SegmentAnnotation['severity'], number>> = { blocking: 0, advisory: 1 };

/** Keeps a DOM id inside `[A-Za-z0-9_-]` (a `p:0` address becomes `p-0`); runs of other characters collapse to one `-`. */
function idPart(value: string): string {
  return value.replace(/[^A-Za-z0-9_-]+/g, '-');
}

function hintAnnotation(hint: QualityHint): SegmentAnnotation {
  const address = hint.segment;
  return {
    id: `ann-${address === null ? 'unresolved' : idPart(address)}-${idPart(hint.ruleId)}`,
    source: 'hint',
    address,
    ruleId: hint.ruleId,
    severity: hint.severity,
    kind: hint.kind,
    messageKey: MESSAGE_KEY_PATTERN.test(hint.messageKey) ? hint.messageKey : UNKNOWN_MESSAGE_KEY,
    evidence: hint.evidence,
    repairDirection: hint.repairDirection,
    keywordIndex: null,
    placement: address === null || address === 'meta' ? 'panel' : 'inline',
  };
}

function keywordAnnotation(flag: KeywordFlag): SegmentAnnotation {
  return {
    id: `ann-${idPart(flag.address)}-${idPart(flag.rule)}-k${flag.index}`,
    source: 'keyword',
    address: flag.address,
    ruleId: flag.rule,
    severity: 'blocking',
    kind: 'objective',
    messageKey: KEYWORD_RULE_MESSAGE_KEYS[flag.rule] ?? KEYWORD_FALLBACK_MESSAGE_KEY,
    evidence: '',
    repairDirection: null,
    keywordIndex: flag.index,
    placement: flag.address === 'meta' ? 'panel' : 'inline',
  };
}

/** Canonical address order, with an unresolved (`null`) address after every real one. */
function compareAnnotationAddresses(left: SegmentAddress | null, right: SegmentAddress | null): number {
  if (left === null) return right === null ? 0 : 1;
  if (right === null) return -1;
  return compareAddresses(left, right);
}

/**
 * The single order of the panel and of every inline slot, so a reader going top-down sees must-fix items first:
 * `kind` (objective first), `severity` (blocking first), `compareAddresses` (null last), then `ruleId`.
 */
export function sortAnnotations(items: readonly SegmentAnnotation[]): readonly SegmentAnnotation[] {
  return Object.freeze([...items].sort((left, right) =>
    KIND_ORDER[left.kind] - KIND_ORDER[right.kind] ||
    SEVERITY_ORDER[left.severity] - SEVERITY_ORDER[right.severity] ||
    compareAnnotationAddresses(left.address, right.address) ||
    (left.ruleId < right.ruleId ? -1 : left.ruleId > right.ruleId ? 1 : 0)));
}

/** `blocking` before `advisory`, then `objective` before `taste`; the kind only breaks a severity tie. */
function isStronger(candidate: SegmentAnnotation, current: SegmentAnnotation): boolean {
  if (SEVERITY_ORDER[candidate.severity] !== SEVERITY_ORDER[current.severity]) return SEVERITY_ORDER[candidate.severity] < SEVERITY_ORDER[current.severity];
  return KIND_ORDER[candidate.kind] < KIND_ORDER[current.kind];
}

/**
 * One id per annotation: the panel and the inline note both use it as a DOM id (the `aria-describedby` target and the
 * `@for` track key), so two survivors must never share one. Two distinct ruleIds can sanitise to the same `idPart`
 * text (`a.b` and `a-b`), which is exactly that case; the later one in sorted output order takes a `-2`, `-3`, … suffix.
 */
function withUniqueIds(items: readonly SegmentAnnotation[]): readonly SegmentAnnotation[] {
  const used = new Set<string>();
  return items.map((item) => {
    if (!used.has(item.id)) {
      used.add(item.id);
      return item;
    }
    let suffix = 2;
    while (used.has(`${item.id}-${String(suffix)}`)) suffix += 1;
    const id = `${item.id}-${String(suffix)}`;
    used.add(id);
    return { ...item, id };
  });
}

/**
 * Merge saved hints and keyword flags into one sorted list. Duplicates collapse on
 * `(source, address, ruleId, keywordIndex)`: the **stronger** of the two wins (blocking over advisory, then objective
 * over taste), and an exact rank tie keeps the first input. Sorting happens before the ids are made unique, so a
 * collision is resolved on the list the reader actually sees.
 */
export function mergeAnnotations(hints: readonly QualityHint[], keywordFlags: readonly KeywordFlag[]): readonly SegmentAnnotation[] {
  const merged: SegmentAnnotation[] = [];
  const at = new Map<string, number>();
  const add = (annotation: SegmentAnnotation, identity: string): void => {
    const index = at.get(identity);
    if (index === undefined) {
      at.set(identity, merged.length);
      merged.push(annotation);
      return;
    }
    const current = merged[index];
    if (current !== undefined && isStronger(annotation, current)) merged[index] = annotation;
  };
  for (const hint of hints) add(hintAnnotation(hint), `hint|${hint.segment ?? ''}|${hint.ruleId}|null`);
  for (const flag of keywordFlags) add(keywordAnnotation(flag), `keyword|${flag.address}|${flag.rule}|${String(flag.index)}`);
  return Object.freeze(withUniqueIds(sortAnnotations(merged)));
}

function sameStrings(left: readonly string[], right: readonly string[]): boolean {
  return left.length === right.length && left.every((value, index) => value === right[index]);
}

function sameSegment(left: StorySegment, right: StorySegment): boolean {
  return left.text === right.text && sameStrings(left.keywords, right.keywords);
}

/**
 * Canonical addresses whose text/keywords differ between two drafts (10.5.10-T1): title, hook, `p:<i>` by index
 * (an added or removed paragraph lists its index), ending, and `meta` for description/tags/contentWarnings.
 */
export function diffSegmentAddresses(mine: StoryDraft, theirs: StoryDraft): readonly SegmentAddress[] {
  const changed: SegmentAddress[] = [];
  if (mine.title !== theirs.title) changed.push('title');
  if (!sameSegment(mine.hook, theirs.hook)) changed.push('hook');
  const paragraphs = Math.max(mine.paragraphs.length, theirs.paragraphs.length);
  for (let index = 0; index < paragraphs; index += 1) {
    const left = mine.paragraphs[index];
    const right = theirs.paragraphs[index];
    if (left === undefined || right === undefined || !sameSegment(left, right)) changed.push(addressForParagraph(index));
  }
  if (!sameSegment(mine.ending, theirs.ending)) changed.push('ending');
  if (mine.description !== theirs.description || !sameStrings(mine.tags, theirs.tags) || !sameStrings(mine.contentWarnings, theirs.contentWarnings)) changed.push('meta');
  return Object.freeze(changed);
}
