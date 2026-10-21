import { readTokenFromHash, stripHash } from './reset-token';

describe('readTokenFromHash', () => {
  it.each([
    ['#t=abc', 'abc'],
    ['#t=A-b_9', 'A-b_9'],
  ])('ResetToken_Read_%s', (hash, token) => {
    expect(readTokenFromHash(hash)).toBe(token);
  });

  it.each(['', '#', '#t=', '#t=abc&x=1', '#t=abc&t=def', '#x=abc', 't=abc', '#t=a b', '#t=a/b', '#T=abc', '#t=abc#t=def', '?t=abc'])(
    'ResetToken_Read_RejectsMalformed_%j',
    (hash) => {
      expect(readTokenFromHash(hash)).toBeNull();
    },
  );
});

describe('stripHash', () => {
  afterEach(() => {
    Reflect.deleteProperty(history, 'replaceState');
    history.replaceState(null, '', '/');
  });

  it('ResetToken_Strip_RemovesFragmentKeepsPathAndSearch', () => {
    history.replaceState(null, '', '/reset-password?a=1#t=secret');
    stripHash();
    expect(location.hash).toBe('');
    expect(location.pathname + location.search).toBe('/reset-password?a=1');
  });

  it('ResetToken_Strip_ToleratesMissingReplaceState', () => {
    history.replaceState(null, '', '/reset-password#t=secret');
    Object.defineProperty(history, 'replaceState', { value: undefined, configurable: true });
    expect(() => stripHash()).not.toThrow();
    expect(location.hash).toBe('#t=secret');
  });
});
