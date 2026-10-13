import { diffWordsWithSpace } from 'diff';
import type { Change } from 'diff';

/**
 * Word-level diff over jsdiff 9 (FE14), pure and Angular-free (README D2). Both sides are normalised before the
 * comparison — NFC, CRLF/CR to LF, whitespace runs collapsed to one space and trimmed — so a whitespace-only edit
 * never renders as a difference.
 *
 * Three bounds keep this cheap enough for a keystroke-adjacent computed: a per-side token cap (tails only), a Myers
 * edit-length ceiling and a rendered-segment ceiling. Anything past a bound answers with the coarse block diff and
 * `truncated`, so the panel shows its truncation notice instead of a misleading (or unbounded) diff.
 */
export type DiffKind = 'equal' | 'insert' | 'delete';
export interface DiffSegment { kind: DiffKind; text: string; }
export interface WordDiff { segments: DiffSegment[]; added: number; removed: number; truncated: boolean; }

/** Per-side token cap: a longer body is compared on its TAILS only. */
export const MAX_DIFF_TOKENS = 20_000;
/**
 * Myers edit-script ceiling. jsdiff's cost follows the edit DISTANCE, not just the input size: a full rewrite of a
 * 20 000-token side against `maxEditLength: 20 000` takes ~13 s, while the same rewrite at this ceiling bails out in
 * ~90 ms (measured 2026-10-13 on this workstation through this exact call, jsdiff 9.0.0; ~157 ms at 2 000, ~350 ms at
 * 3 000). 1 500 still diffs a heavily revised story normally and keeps the worst case inside one frame budget.
 */
export const MAX_EDIT_LENGTH = 1_500;
/** Rendered-size ceiling: a pair that needs more segments than this is a rewrite, so the coarse answer is honest. */
export const MAX_DIFF_SEGMENTS = 400;

function normalize(text: string): string {
  return text.normalize('NFC').replace(/\r\n?/g, '\n').replace(/\s+/g, ' ').trim();
}

/** Non-whitespace runs only, so a segment of pure spacing counts as zero tokens. */
function countTokens(text: string): number {
  const matches = text.match(/\S+/g);
  return matches === null ? 0 : matches.length;
}

/** The last `maxTokens` word tokens of a normalised string; `truncated` is true only when tokens were dropped. */
function tailByTokens(text: string, maxTokens: number): { text: string; truncated: boolean } {
  const pattern = /\S+/g;
  let total = 0;
  while (pattern.exec(text) !== null) total++;
  if (total <= maxTokens) return { text, truncated: false };
  const skip = total - maxTokens;
  let index = 0;
  let start = text.length;
  for (let found = pattern.exec(text); found !== null; found = pattern.exec(text)) {
    if (index === skip) { start = found.index; break; }
    index++;
  }
  return { text: text.slice(start), truncated: true };
}

/** Wrong-but-bounded answer for a diff past any bound: one delete block + one insert block, flagged truncated. */
function coarse(left: string, right: string): WordDiff {
  const segments: DiffSegment[] = [];
  let removed = 0;
  let added = 0;
  if (left !== '') { segments.push({ kind: 'delete', text: left }); removed = countTokens(left); }
  if (right !== '') { segments.push({ kind: 'insert', text: right }); added = countTokens(right); }
  return { segments, added, removed, truncated: true };
}

function compute(left: string, right: string): WordDiff {
  const a = tailByTokens(normalize(left), MAX_DIFF_TOKENS);
  const b = tailByTokens(normalize(right), MAX_DIFF_TOKENS);
  const truncated = a.truncated || b.truncated;
  const changes: readonly Change[] | undefined = diffWordsWithSpace(a.text, b.text, { maxEditLength: MAX_EDIT_LENGTH });
  if (changes === undefined) return coarse(a.text, b.text);
  const segments: DiffSegment[] = [];
  let added = 0;
  let removed = 0;
  for (const change of changes) {
    if (change.value === '') continue;
    const kind: DiffKind = change.added === true ? 'insert' : change.removed === true ? 'delete' : 'equal';
    if (kind === 'insert') added += countTokens(change.value);
    else if (kind === 'delete') removed += countTokens(change.value);
    const last = segments[segments.length - 1];
    if (last !== undefined && last.kind === kind) last.text += change.value;
    else segments.push({ kind, text: change.value });
  }
  if (segments.length > MAX_DIFF_SEGMENTS) return coarse(a.text, b.text);
  return { segments, added, removed, truncated };
}

let memoLeft: string | null = null;
let memoRight: string | null = null;
let memoDiff: WordDiff | null = null;

/**
 * One-entry memo keyed by the two compared strings: `VersionsStore.diff` and the deferred `DiffView` both ask for the
 * same pair, so the pair is diffed once. Module-level and strings only; a different pair recomputes and replaces it.
 * The result is shared, so callers must treat it as immutable.
 */
export function diffWords(left: string, right: string): WordDiff {
  if (memoDiff !== null && memoLeft === left && memoRight === right) return memoDiff;
  const result = compute(left, right);
  memoLeft = left;
  memoRight = right;
  memoDiff = result;
  return result;
}
