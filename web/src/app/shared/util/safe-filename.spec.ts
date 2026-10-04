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
    // Further invisible / spoofing characters: each is dropped, never replaced.
    ['a\u061Cb', 'ab'],
    ['a\u2060b\u2061c\u2062d\u2063e\u2064f', 'abcdef'],
    ['a\u206Ab\u206Bc\u206Cd\u206De\u206Ef\u206F', 'abcdef'],
    ['a\u2066b\u2067c\u2068d\u2069e', 'abcde'],
    ['a\u00ADb', 'ab'],
    ['a\u034Fb', 'ab'],
    ['a\u180Bb\u180Cc\u180Dd\u180Ee', 'abcde'],
    ['a\uFE00b\uFE0Fc', 'abc'],
    ['a\u115Fb\u1160c', 'abc'],
    ['a\u3164b', 'ab'],
    ['a\uFFA0b', 'ab'],
    ['a\u{E0000}b\u{E0041}c\u{E007F}d', 'abcd'],
    // A device name hidden behind an invisible character is still a device name.
    ['C\u00ADON', '_CON'],
    ['.\u0000.', 'download'],
    ['...', 'download'],
    ['.', 'download'],
    ['""', 'download'],
    ['', 'download'],
    ['   ', 'download'],
    ['a..b', 'ab'],
    ['  spaced   out  .mp4 ', 'spaced out .mp4'],
    ['C:\\Windows\\system32', 'C_Windowssystem32'],
    // Windows device names, with or without an extension, any case.
    ['CON', '_CON'],
    ['con', '_con'],
    ['nul.txt', '_nul.txt'],
    ['PRN.tar.gz', '_PRN.tar.gz'],
    ['aux', '_aux'],
    ['COM1.txt', '_COM1.txt'],
    ['Com9', '_Com9'],
    ['lpt1.log', '_lpt1.log'],
    ['LPT9', '_LPT9'],
    // Superscript digits are reserved too (Windows: COM¹ COM² COM³ LPT¹ LPT² LPT³).
    ['COM\u00B9', '_COM\u00B9'],
    ['com\u00B2.txt', '_com\u00B2.txt'],
    ['Com\u00B3.tar.gz', '_Com\u00B3.tar.gz'],
    ['LPT\u00B9', '_LPT\u00B9'],
    ['lpt\u00B2 .log', '_lpt\u00B2 .log'],
    ['Lpt\u00B3.', '_Lpt\u00B3'],
    // Not device names: a different superscript, or more text after the stem.
    ['COM\u2074', 'COM\u2074'],
    ['com\u00B9\u00B2.txt', 'com\u00B9\u00B2.txt'],
    ['COM\u00B9 x', 'COM\u00B9 x'],
    // Not device names: no reserved stem.
    ['console.log', 'console.log'],
    ['com10.txt', 'com10.txt'],
    ['com0.txt', 'com0.txt'],
    ['nullable', 'nullable'],
    // Characters Windows forbids in a name.
    ['a:b.txt', 'a_b.txt'],
    ['a.txt:evil.exe', 'a.txt_evil.exe'],
    ['x<y>z|w*?.txt', 'x_y_z_w__.txt'],
    // Trailing dot / space (Windows strips them silently, which can re-expose a reserved or hidden name).
    ['file.', 'file'],
    ['file. ', 'file'],
    ['file .', 'file'],
    ['CON. ', '_CON'],
    // Windows also strips dots/spaces between the device stem and the extension.
    ['CON .txt', '_CON .txt'],
    ['NUL.', '_NUL'],
    ['nul .', '_nul'],
    ['Com1 . log', '_Com1 . log'],
    // Not device names: a stem followed by other text.
    ['con x.txt', 'con x.txt'],
    ['com1 x', 'com1 x'],
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
