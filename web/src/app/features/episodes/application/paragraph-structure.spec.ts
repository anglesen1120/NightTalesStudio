import { applyMove, applyRemove, insertAt } from './paragraph-structure';

const list: readonly string[] = Object.freeze(['a', 'b', 'c', 'd']);

describe('paragraph-structure', () => {
  it('applyMove_ToStart_MovesTheItemAndKeepsTheRest', () => {
    expect(applyMove(list, 2, 0)).toEqual(['c', 'a', 'b', 'd']);
    expect(applyMove(list, 3, 1)).toEqual(['a', 'd', 'b', 'c']);
  });

  it('applyMove_ToEnd_MovesTheItemLast', () => {
    expect(applyMove(list, 0, 3)).toEqual(['b', 'c', 'd', 'a']);
    expect(applyMove(list, 2, 99)).toEqual(['a', 'b', 'd', 'c']);
  });

  it('applyMove_SameIndex_IsStable_AndReturnsTheSameArray', () => {
    expect(applyMove(list, 2, 2)).toBe(list);
    expect(applyMove(list, 4, 0)).toBe(list);
    expect(applyMove(list, 0, 3.5)).toBe(list);
    expect(applyMove(list, -1, 0)).toBe(list);
  });

  it('applyMove_ClampsOutOfRangeTarget', () => {
    expect(applyMove(list, 1, 99)).toEqual(applyMove(list, 1, 3));
    expect(applyMove(list, 1, -5)).toEqual(applyMove(list, 1, 0));
  });

  it('applyMove_DoesNotMutateTheInput_AndFreezesTheResult', () => {
    const input = ['a', 'b', 'c'];
    const moved = applyMove(input, 0, 2);
    expect(input).toEqual(['a', 'b', 'c']);
    expect(Object.isFrozen(moved)).toBe(true);
    expect(() => (moved as string[]).push('d')).toThrow(TypeError);
  });

  it('applyRemove_ThenInsertAt_RestoresOrder', () => {
    const removed = applyRemove(list, 2);
    expect(removed).toEqual(['a', 'b', 'd']);
    expect(insertAt(removed, 2, list[2] as string)).toEqual([...list]);
  });

  it('applyRemove_OutOfRange_IsStable_AndDoesNotMutateTheInput', () => {
    const input = ['a', 'b'];
    expect(applyRemove(input, 2)).toBe(input);
    expect(applyRemove(input, -1)).toBe(input);
    expect(input).toEqual(['a', 'b']);
    expect(Object.isFrozen(applyRemove(input, 0))).toBe(true);
  });

  it('insertAt_ClampsToTheEnds_AndFreezesTheResult', () => {
    expect(insertAt(['a', 'b'], 99, 'z')).toEqual(['a', 'b', 'z']);
    expect(insertAt(['a', 'b'], -3, 'z')).toEqual(['z', 'a', 'b']);
    expect(insertAt(['a', 'b'], 0.5, 'z')).toEqual(['a', 'b']);
    expect(Object.isFrozen(insertAt(['a', 'b'], 1, 'z'))).toBe(true);
  });
});
