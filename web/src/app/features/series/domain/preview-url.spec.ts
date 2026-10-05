import { safePreviewUrl } from './preview-url';

describe('safePreviewUrl', () => {
  it.each(['https://cdn.example.com/p/a.mp3', 'http://localhost:9000/p/a.mp3', '/media/previews/a.mp3', 'HTTPS://CDN.EXAMPLE.COM/a.mp3'])(
    'accepts %s',
    (url) => expect(safePreviewUrl(url)).toBe(url),
  );

  it.each([
    ['protocol-relative', '//evil.example/a.mp3'],
    ['javascript:', 'javascript:alert(1)'],
    ['JavaScript: mixed case', 'JaVaScRiPt:alert(1)'],
    ['data:', 'data:audio/mp3;base64,AAAA'],
    ['blob:', 'blob:https://example.com/uuid'],
    ['ftp:', 'ftp://example.com/a.mp3'],
    ['relative without slash', 'media/a.mp3'],
    ['backslash path', '/\\evil.example/a.mp3'],
    ['backslash host', 'https:\\\\evil.example\\a.mp3'],
    ['space', 'https://example.com/a b.mp3'],
    ['leading space', ' https://example.com/a.mp3'],
    ['tab', 'https://example.com/a\t.mp3'],
    ['newline', 'https://example.com/a\n.mp3'],
    ['NUL', 'https://example.com/a\u0000.mp3'],
    ['DEL', '/a\u007f.mp3'],
    ['empty', ''],
  ])('rejects %s', (_name, url) => expect(safePreviewUrl(url)).toBeNull());

  it('maps null to null', () => expect(safePreviewUrl(null)).toBeNull());
});
