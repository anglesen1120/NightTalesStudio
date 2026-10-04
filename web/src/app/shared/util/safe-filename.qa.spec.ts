import { safeFilename } from './safe-filename';

// QA (10.1.6-T1): adversarial names beyond the implementer's table. Every assertion is a property the helper must
// hold for ANY server-supplied name; nothing here pins an accidental output.
describe('safeFilename (QA adversarial)', () => {
  const inputs: readonly string[] = [
    '../../../../etc/passwd',
    '..\\..\\Windows\\System32\\cmd.exe',
    '/etc/passwd',
    'C:\\boot.ini',
    'a/./../b',
    './.',
    '. .',
    '.\u200B.',
    '.\u202E.',
    'x\u0000.exe',
    'invoice\u202Egpj.exe',
    'invoice\u2066gpj.exe\u2069',
    'javascript:alert(1)',
    'data:text/html,<script>alert(1)</script>',
    '"><img src=x onerror=alert(1)>.png',
    'CON',
    'nul.txt',
    'COM1.txt',
    'file. ',
    'file.',
    'a'.repeat(300) + '.pdf',
    '😀'.repeat(300),
    'Đêm\u0301-Trắng.mp4',
    '\u2028\u2029line.sep',
    '',
    '\u0000',
    '\u202E',
  ];

  it.each(inputs)('keeps every safety invariant for %j', (raw) => {
    const out = safeFilename(raw);
    expect(out.length).toBeGreaterThan(0);
    expect(Array.from(out).length).toBeLessThanOrEqual(100);
    expect(out).not.toMatch(/["'`/\\]/); // no separator or quote
    expect(out).not.toContain('..'); // no traversal token
    expect(out).toBe(out.normalize('NFC')); // composed form only
    expect(out).toBe(out.trim());
    // No C0/C1 controls, bidi overrides/isolates, zero-width marks or BOM survive.
    expect(out).not.toMatch(/[\u0000-\u001f\u007f-\u009f\u200b-\u200f\u202a-\u202e\u2066-\u2069\ufeff]/);
    // A well-formed string: no lone surrogate from truncation.
    expect(decodeURIComponent(encodeURIComponent(out))).toBe(out); // throws on a lone surrogate
  });

  it('never returns a dot-only name', () => {
    for (const raw of ['.', '..', '...', '. . .', '.\u0000.', '\u202E..']) {
      expect(safeFilename(raw)).not.toMatch(/^\.+$/);
    }
  });

  it('is idempotent: sanitising a sanitised name changes nothing', () => {
    for (const raw of inputs) {
      const once = safeFilename(raw);
      expect(safeFilename(once)).toBe(once);
    }
  });

  it('turns a scheme-looking name into plain data (it only ever feeds a.download, never href)', () => {
    expect(safeFilename('javascript:alert(1)')).toBe('javascript:alert(1)');
  });
});
