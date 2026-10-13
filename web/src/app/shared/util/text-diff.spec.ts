import { MAX_DIFF_TOKENS, diffWords } from './text-diff';
import type { WordDiff } from './text-diff';

/** Word tokens of the joined non-delete side — the diff must still reproduce the right text. */
function rightText(diff: WordDiff): string {
  return diff.segments.filter((segment) => segment.kind !== 'delete').map((segment) => segment.text).join('');
}

describe('diffWords', () => {
  it('identical input is one equal run with zero counts', () => {
    const diff = diffWords('alpha beta gamma', 'alpha beta gamma');
    expect(diff.added).toBe(0);
    expect(diff.removed).toBe(0);
    expect(diff.truncated).toBe(false);
    expect(diff.segments.every((segment) => segment.kind === 'equal')).toBe(true);
  });

  it('empty on both sides has no added or removed tokens', () => {
    const diff = diffWords('', '');
    expect(diff.added).toBe(0);
    expect(diff.removed).toBe(0);
    expect(diff.segments).toEqual([]);
  });

  it('a pure insert reports the inserted token count', () => {
    const diff = diffWords('', 'alpha beta');
    expect(diff.added).toBe(2);
    expect(diff.removed).toBe(0);
    expect(diff.segments).toEqual([{ kind: 'insert', text: 'alpha beta' }]);
  });

  it('a pure delete reports the deleted token count', () => {
    const diff = diffWords('alpha beta', '');
    expect(diff.added).toBe(0);
    expect(diff.removed).toBe(2);
    expect(diff.segments).toEqual([{ kind: 'delete', text: 'alpha beta' }]);
  });

  it('a single word change yields one delete and one insert at token level', () => {
    const diff = diffWords('alpha beta gamma', 'alpha delta gamma');
    expect(diff.added).toBe(1);
    expect(diff.removed).toBe(1);
    expect(rightText(diff)).toBe('alpha delta gamma');
    expect(diff.segments.map((segment) => segment.kind)).toContain('insert');
    expect(diff.segments.map((segment) => segment.kind)).toContain('delete');
  });

  it('NFC- and NFD-encoded text compare equal', () => {
    const diff = diffWords('café déjà vu', 'cafe\u0301 de\u0301ja\u0300 vu');
    expect(diff.added).toBe(0);
    expect(diff.removed).toBe(0);
  });

  it('CRLF and LF line endings compare equal', () => {
    const diff = diffWords('line one\r\nline two', 'line one\nline two');
    expect(diff.added).toBe(0);
    expect(diff.removed).toBe(0);
  });

  it('collapsed whitespace is not a difference', () => {
    const diff = diffWords('a   b', ' a b ');
    expect(diff.added).toBe(0);
    expect(diff.removed).toBe(0);
  });

  it('counts only word tokens, never whitespace-only segments', () => {
    const diff = diffWords('one', 'one two three');
    expect(diff.added).toBe(2);
    expect(diff.removed).toBe(0);
  });

  it('markup in the content stays plain text and is never executed', () => {
    const payload = '<script>alert(1)</script>';
    const diff = diffWords('safe', `safe ${payload}`);
    expect(diff.segments.filter((segment) => segment.kind === 'insert').map((segment) => segment.text).join('')).toContain(payload);
    expect(diff.added).toBeGreaterThan(0);
  });

  it('caps each side at MAX_DIFF_TOKENS tails and flags truncation', () => {
    const head = Array.from({ length: 50 }, (_, index) => `head-${String(index)}`).join(' ');
    const tail = Array.from({ length: MAX_DIFF_TOKENS + 500 }, (_, index) => `w${String(index)}`).join(' ');
    const diff = diffWords(`${head} ${tail}`, `${head} ${tail}`);
    expect(diff.truncated).toBe(true);
    expect(diff.added).toBe(0);
    expect(diff.removed).toBe(0);
  });

  it('a long 60-paragraph pair completes well under the 250 ms bound', () => {
    const filler = 'lorem ipsum dolor sit amet consectetur adipiscing elit sed do eiusmod tempor incididunt '.repeat(8);
    const paragraph = (index: number, mark = ''): string => `Paragraph ${String(index)} ${mark}${filler}`;
    const left = Array.from({ length: 60 }, (_, index) => paragraph(index)).join('\n\n');
    const right = Array.from({ length: 60 }, (_, index) => paragraph(index, index === 30 ? 'REVISED ' : '')).join('\n\n');
    expect(left.length).toBeGreaterThan(35_000);
    const started = performance.now();
    const diff = diffWords(left, right);
    const elapsed = performance.now() - started;
    expect(elapsed).toBeLessThan(250);
    expect(diff.truncated).toBe(false);
    expect(diff.added).toBeGreaterThan(0);
  });
});
