import { MAX_DIFF_TOKENS, diffWords } from './text-diff';
import type { WordDiff } from './text-diff';

/** The right-hand text recovered from the segments (deletes are dropped) — the diff must not lose or add words. */
function rightText(diff: WordDiff): string {
  return diff.segments.filter((segment) => segment.kind !== 'delete').map((segment) => segment.text).join('');
}

/** `n` distinct-ish word tokens joined by single spaces (already in the shape `normalize` produces). */
function words(count: number, pick: (index: number) => string): string {
  return Array.from({ length: count }, (_, index) => pick(index)).join(' ');
}

/**
 * Exploratory/boundary coverage for the shared word diff (QA of 10.5.4-T1, not the author's spec).
 *
 * The author's `text-diff.spec.ts` covers the happy paths. These tests target the edges the panel actually hits:
 * the exact truncation boundary, an adversarial prefix far beyond the cap, a very long alternating pair, and the
 * exotic-but-possible story characters (lone surrogates, combining marks, RTL, bare CR, markup).
 *
 * NOTE (measured, 2026-10-13, this workstation): `MAX_EDIT_LENGTH` bounds the WORK, not just the input size. A fully
 * rewritten 20 000-token side bails to the coarse answer in ~11 ms at 500, ~89 ms at 1 500 (the shipped ceiling) and
 * ~350 ms at 3 000, against ~13 s for an 8k rewrite before that ceiling existed. The author's `text-diff.spec.ts`
 * pins the wall-clock bound and the answer size; wall-clock numbers stay out of this file (machine-dependent).
 */
describe('diffWords (QA boundaries)', () => {
  it('exactly MAX_DIFF_TOKENS per side is not truncated and still reconstructs the right text', () => {
    const left = words(MAX_DIFF_TOKENS, (index) => `l${String(index)}`);
    const right = words(MAX_DIFF_TOKENS, (index) => (index === 0 ? 'changed' : `l${String(index)}`));
    const diff = diffWords(left, right);
    expect(diff.truncated).toBe(false);
    expect(diff.added).toBe(1);
    expect(diff.removed).toBe(1);
    expect(rightText(diff)).toBe(right);
  });

  it('one token over the cap drops the prefix entirely and compares tails only', () => {
    const tail = words(MAX_DIFF_TOKENS, (index) => `t${String(index)}`);
    const left = `dropped-left ${tail}`;
    const right = `dropped-right ${tail}`;
    const diff = diffWords(left, right);
    expect(diff.truncated).toBe(true);
    // The differing prefixes are outside the kept tails, so they must never reach the differ.
    expect(diff.added).toBe(0);
    expect(diff.removed).toBe(0);
    expect(rightText(diff)).not.toContain('dropped-left');
    expect(rightText(diff)).not.toContain('dropped-right');
  });

  it('a very long pair of tiny alternations stays bounded and reconstructs the right side', () => {
    const left = words(MAX_DIFF_TOKENS, (index) => (index % 2 === 0 ? 'aye' : 'bee'));
    const right = words(MAX_DIFF_TOKENS, (index) => (index % 2 === 0 ? 'bee' : 'aye'));
    const diff = diffWords(left, right);
    expect(diff.truncated).toBe(false);
    expect(rightText(diff)).toBe(right);
    // The bound is structural: neither count can exceed the per-side cap.
    expect(diff.added).toBeLessThanOrEqual(MAX_DIFF_TOKENS);
    expect(diff.removed).toBeLessThanOrEqual(MAX_DIFF_TOKENS);
    expect(diff.segments.length).toBeLessThanOrEqual(2 * MAX_DIFF_TOKENS + 1);
  });

  it('a lone surrogate never throws and stays in the compared text', () => {
    const diff = diffWords(`\uD800 orphan`, 'orphan');
    expect(diff.removed).toBe(1);
    expect(diff.segments.map((segment) => segment.text).join('')).toContain('\uD800');
  });

  it('combining marks and precomposed accents are the same text', () => {
    const diff = diffWords('cafe\u0301 au lait', 'caf\u00E9 au lait');
    expect(diff.added).toBe(0);
    expect(diff.removed).toBe(0);
    expect(diff.truncated).toBe(false);
  });

  it('right-to-left text is preserved and diffs at the token level', () => {
    const diff = diffWords('\u05E9\u05DC\u05D5\u05DD \u05E2\u05D5\u05DC\u05DD', '\u05E9\u05DC\u05D5\u05DD \u05E2\u05D5\u05DC\u05DD \u05D8\u05D5\u05D1');
    expect(diff.added).toBe(1);
    expect(diff.removed).toBe(0);
    expect(rightText(diff)).toContain('\u05D8\u05D5\u05D1');
  });

  it('bare CR, CRLF and LF are one line break', () => {
    const diff = diffWords('line one\rline two', 'line one\r\nline two');
    expect(diff.added).toBe(0);
    expect(diff.removed).toBe(0);
  });

  it('an empty side is a pure insert or delete, never an equal-only result', () => {
    const inserted = diffWords('', 'one two three');
    expect(inserted.added).toBe(3);
    expect(inserted.removed).toBe(0);
    expect(inserted.truncated).toBe(false);
    const deleted = diffWords('one two three', '');
    expect(deleted.removed).toBe(3);
    expect(deleted.added).toBe(0);
    const both = diffWords('', '');
    expect(both.segments).toEqual([]);
    expect(both.added).toBe(0);
    expect(both.removed).toBe(0);
  });

  it('markup in a version body is compared as plain text and never reordered', () => {
    const body = '<script>alert(1)</script> & <b>bold</b>';
    const diff = diffWords(body, body);
    expect(diff.added).toBe(0);
    expect(diff.removed).toBe(0);
    expect(diff.segments.map((segment) => segment.text).join('')).toBe(body);
  });
});
