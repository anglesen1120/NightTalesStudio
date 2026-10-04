import { CURSOR_PAGE_MAX_LIMIT, cursorPage } from './cursor-page';

// QA (10.2.6-T1): boundary values of the limit validator and the cursor normalisation.
describe('cursorPage (QA boundaries)', () => {
  it('accepts the inclusive bounds 1 and 100', () => {
    expect(cursorPage(1)).toStrictEqual({ limit: 1 });
    expect(cursorPage(CURSOR_PAGE_MAX_LIMIT)).toStrictEqual({ limit: 100 });
  });

  it.each([0, -1, 101, 1.5, Number.NaN, Number.POSITIVE_INFINITY, Number.NEGATIVE_INFINITY, 100.0000001, 1e9])(
    'rejects limit %s with a RangeError',
    (limit) => {
      expect(() => cursorPage(limit)).toThrow(RangeError);
    },
  );

  it('rejects a limit that is not a number at runtime (string/null/undefined)', () => {
    for (const bad of ['10', null, undefined, {}, []]) {
      expect(() => cursorPage(bad as number)).toThrow(RangeError);
    }
  });

  it('keeps an opaque cursor verbatim and never trims or re-encodes it', () => {
    expect(cursorPage(5, ' a/b+c=? ')).toStrictEqual({ cursor: ' a/b+c=? ', limit: 5 });
  });

  it('validates the limit before looking at the cursor', () => {
    expect(() => cursorPage(0, 'abc')).toThrow(RangeError);
  });
});
