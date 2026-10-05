import { describe, expect, it } from 'vitest';
import { CURSOR_PAGE_MAX_LIMIT, type CursorPage, cursorPage } from './cursor-page';

describe('cursorPage', () => {
  it('cursorPage_FirstPage_HasLimitAndNoCursorMember', () => {
    expect(cursorPage(20)).toStrictEqual({ limit: 20 });
    expect(cursorPage(20, null)).toStrictEqual({ limit: 20 });
    expect(cursorPage(20, '')).toStrictEqual({ limit: 20 });
  });

  it('cursorPage_NextPage_CarriesTheOpaqueCursorUntouched', () => {
    expect(cursorPage(20, 'abc_-123')).toStrictEqual({ cursor: 'abc_-123', limit: 20 });
  });

  it('cursorPage_LimitBounds_AreInclusive', () => {
    expect(cursorPage(1)).toStrictEqual({ limit: 1 });
    expect(cursorPage(CURSOR_PAGE_MAX_LIMIT)).toStrictEqual({ limit: 100 });
  });

  it.each([101, 1000, 0, -1, 1.5, Number.NaN, Number.POSITIVE_INFINITY])(
    'cursorPage_InvalidLimit_%s_IsRejectedBeforeAnyCall',
    (limit) => {
      let calls = 0;
      const fetch = (): void => {
        calls += 1;
      };
      expect(() => {
        cursorPage(limit);
        fetch();
      }).toThrow(RangeError);
      expect(calls).toBe(0);
    },
  );

  it('CursorPage_WireShape_KeepsItemsAndNextCursor', () => {
    const page: CursorPage<string> = { items: ['a', 'b'], nextCursor: 'c1' };
    expect(Object.keys(page).sort()).toStrictEqual(['items', 'nextCursor']);
    const last: CursorPage<string> = { items: [], nextCursor: null };
    expect(last.nextCursor).toBeNull();
  });
});
