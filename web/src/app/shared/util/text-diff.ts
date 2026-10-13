import { diffWordsWithSpace } from 'diff';
import type { Change } from 'diff';

/**
 * Word-level diff over jsdiff 9 (FE14), pure and Angular-free (README D2). Both sides are normalised before the
 * comparison — NFC, CRLF/CR to LF, whitespace runs collapsed to one space and trimmed — so a whitespace-only edit
 * never renders as a difference. Each side is capped at {@link MAX_DIFF_TOKENS} word tokens: past the cap only the
 * TAILS are compared and `truncated` is set, which also keeps the O(n·m) work bounded on a long story (C9).
 */
export type DiffKind = 'equal' | 'insert' | 'delete';
export interface DiffSegment { kind: DiffKind; text: string; }
export interface WordDiff { segments: DiffSegment[]; added: number; removed: number; truncated: boolean; }
export const MAX_DIFF_TOKENS = 20_000;

/** Myers edit-script ceiling; a longer script bails out instead of running unbounded. */
const MAX_EDIT_LENGTH = MAX_DIFF_TOKENS;

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

/** Wrong-but-bounded answer for a refused diff: one delete block + one insert block, flagged truncated. */
function coarse(left: string, right: string): WordDiff {
  const segments: DiffSegment[] = [];
  let removed = 0;
  let added = 0;
  if (left !== '') { segments.push({ kind: 'delete', text: left }); removed = countTokens(left); }
  if (right !== '') { segments.push({ kind: 'insert', text: right }); added = countTokens(right); }
  return { segments, added, removed, truncated: true };
}

export function diffWords(left: string, right: string): WordDiff {
  const a = tailByTokens(normalize(left), MAX_DIFF_TOKENS);
  const b = tailByTokens(normalize(right), MAX_DIFF_TOKENS);
  const truncated = a.truncated || b.truncated;
  const options = { maxEditLength: MAX_EDIT_LENGTH };
  const changes: readonly Change[] | undefined = diffWordsWithSpace(a.text, b.text, options);
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
  return { segments, added, removed, truncated };
}
