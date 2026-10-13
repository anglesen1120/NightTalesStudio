/**
 * Pure, immutable paragraph-list operations for the story editor (10.5.9-T1).
 *
 * Every result is a frozen array, so a `signal` holding one can be compared by reference: `@for (…) track segKey`
 * reuses every untouched row and a no-op returns the *same* array (no dirty state, no re-render).
 */

/**
 * Move `from` to `to` (a CDK drop end). `to` is clamped to `[0, length - 1]`; a move onto itself, an out-of-range
 * `from` or a non-integer argument returns the input array unchanged.
 */
export function applyMove<T>(paragraphs: readonly T[], from: number, to: number): readonly T[] {
  if (!Number.isInteger(from) || from < 0 || from >= paragraphs.length || !Number.isInteger(to)) return paragraphs;
  const target = to < 0 ? 0 : to > paragraphs.length - 1 ? paragraphs.length - 1 : to;
  if (target === from) return paragraphs;
  const next = [...paragraphs];
  const [moved] = next.splice(from, 1);
  next.splice(target, 0, moved as T);
  return Object.freeze(next);
}

/** Remove `index`; an out-of-range index returns the input array unchanged. */
export function applyRemove<T>(paragraphs: readonly T[], index: number): readonly T[] {
  if (!Number.isInteger(index) || index < 0 || index >= paragraphs.length) return paragraphs;
  return Object.freeze([...paragraphs.slice(0, index), ...paragraphs.slice(index + 1)]);
}

/** Insert `item` at `index` (undo); `index` is clamped to `[0, length]`. */
export function insertAt<T>(paragraphs: readonly T[], index: number, item: T): readonly T[] {
  if (!Number.isInteger(index)) return paragraphs;
  const target = index < 0 ? 0 : index > paragraphs.length ? paragraphs.length : index;
  return Object.freeze([...paragraphs.slice(0, target), item, ...paragraphs.slice(target)]);
}
