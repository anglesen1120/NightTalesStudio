import { safeFilename } from './safe-filename';

describe('safeFilename', () => {
  const hostile: readonly (readonly [string, string])[] = [
    ['../etc/passwd', 'etcpasswd'],
    ['..\\win', 'win'],
    ['"quoted".mp4', 'quoted.mp4'],
    ["it's.mp4", 'its.mp4'],
    ['a\r\nb.mp4', 'ab.mp4'],
    ['a\u0000b', 'ab'],
    ['a\u0085b\u009fc\u007f', 'abc'],
    ['gpj.\u202Eexe', 'gpj.exe'],
    ['a\u200Bb\ufeffc', 'abc'],
    ['.\u0000.', 'download'],
    ['...', 'download'],
    ['.', 'download'],
    ['""', 'download'],
    ['', 'download'],
    ['   ', 'download'],
    ['a..b', 'ab'],
    ['  spaced   out  .mp4 ', 'spaced out .mp4'],
    ['C:\\Windows\\system32', 'C:Windowssystem32'],
  ];

  it.each(hostile)('sanitises %j to %j', (raw, expected) => {
    expect(safeFilename(raw)).toBe(expected);
  });

  it('never lets a separator, control character or ".." survive any table entry', () => {
    for (const [raw] of hostile) {
      const out = safeFilename(raw);
      expect(out).not.toMatch(/["'`/\\]/);
      expect(out).not.toContain('..');
      const codes = Array.from(out, (character) => character.codePointAt(0) ?? 0);
      expect(codes.filter((code) => code <= 0x1f || (code >= 0x7f && code <= 0x9f))).toEqual([]);
      expect(out.length).toBeGreaterThan(0);
    }
  });

  it('truncates to 100 characters', () => {
    expect(safeFilename('a'.repeat(300))).toHaveLength(100);
    expect(safeFilename('a'.repeat(100))).toHaveLength(100);
    expect(safeFilename('a'.repeat(101))).toHaveLength(100);
  });

  it('truncates by code point and never splits a surrogate pair', () => {
    const out = safeFilename('😀'.repeat(150));
    expect(Array.from(out)).toHaveLength(100);
    expect(out).toBe('😀'.repeat(100));
  });

  it('keeps Vietnamese diacritics and returns NFC even from NFD input', () => {
    expect(safeFilename('Đêm-Trắng.mp4')).toBe('Đêm-Trắng.mp4');
    expect(safeFilename('báo-cáo tháng 3.pdf')).toBe('báo-cáo tháng 3.pdf');
    const nfd = 'ba\u0301o-ca\u0301o.pdf';
    expect(safeFilename(nfd)).toBe('báo-cáo.pdf');
    expect(safeFilename(nfd)).toBe(safeFilename(nfd).normalize('NFC'));
  });

  it('uses the supplied fallback only when nothing usable is left', () => {
    expect(safeFilename('..', 'episode.mp4')).toBe('episode.mp4');
    expect(safeFilename('"', 'episode.mp4')).toBe('episode.mp4');
    expect(safeFilename('ok.mp4', 'episode.mp4')).toBe('ok.mp4');
  });
});
