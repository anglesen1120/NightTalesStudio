const MAX_LENGTH = 100;

/**
 * C0 + DEL + C1 controls, and the invisible characters that spoof a name: zero-width marks, bidi
 * controls (U+202E turns "gpj.exe" into "exe.jpg" on screen), soft hyphens, combining grapheme
 * joiners, Mongolian format characters, Hangul fillers, variation selectors and TAG characters.
 * Dropped, not replaced: a replacement would still reach the filesystem.
 */
function isControlOrSpoofing(codePoint: number): boolean {
  return (
    codePoint <= 0x1f ||
    (codePoint >= 0x7f && codePoint <= 0x9f) ||
    codePoint === 0xad ||
    codePoint === 0x34f ||
    codePoint === 0x61c ||
    (codePoint >= 0x180b && codePoint <= 0x180e) ||
    codePoint === 0x115f ||
    codePoint === 0x1160 ||
    codePoint === 0x3164 ||
    codePoint === 0xffa0 ||
    (codePoint >= 0x200b && codePoint <= 0x200f) ||
    (codePoint >= 0x202a && codePoint <= 0x202e) ||
    (codePoint >= 0x2060 && codePoint <= 0x206f) ||
    (codePoint >= 0xfe00 && codePoint <= 0xfe0f) ||
    codePoint === 0xfeff ||
    (codePoint >= 0xe0000 && codePoint <= 0xe007f)
  );
}

/**
 * Windows device names: reserved with or without an extension, in any case (`CON`, `nul.txt`,
 * `Com1.tar.gz`). Windows silently strips trailing dots and spaces off the stem, so `CON .txt` and
 * `NUL.` are reserved too — the stem is followed by dots/spaces and then an extension or the end.
 * The superscript digits `¹ ² ³` (U+00B9, U+00B2, U+00B3) are reserved as well (`COM¹`, `LPT²`): Windows
 * lists them as device names, and NFC leaves them untouched, so they must be matched explicitly.
 */
const WINDOWS_RESERVED = /^(?:con|prn|aux|nul|(?:com|lpt)[1-9\u00b9\u00b2\u00b3])[. ]*(?:\.|$)/i;

/**
 * Makes a file name that came from the server (`Content-Disposition`, API field) safe to hand to `a.download`
 * (README D4). NFC -> drop quotes, `/`, `\` and `..` -> drop control characters -> replace the characters Windows
 * forbids (`: < > | * ?`) with `_` -> collapse whitespace -> trim -> drop trailing dots and spaces -> prefix `_` to a
 * Windows device name -> truncate to 100 characters -> `fallback` when nothing but dots (or nothing) is left.
 * Vietnamese diacritics are kept and stay NFC.
 */
export function safeFilename(raw: string, fallback = 'download'): string {
  const visible = Array.from(String(raw).normalize('NFC'))
    .filter((character) => !isControlOrSpoofing(character.codePointAt(0) ?? 0))
    .join('');
  // Controls are dropped before the dot sweep so `.<NUL>.` collapses to `..` and is removed too.
  const cleaned = visible
    .replace(/["'`/\\]/g, '')
    .replace(/\.{2,}/g, '')
    .replace(/[:<>|*?]/g, '_')
    .replace(/\s+/g, ' ')
    .trim()
    .replace(/[. ]+$/, '');
  if (cleaned === '' || /^\.+$/.test(cleaned)) return fallback;
  const named = WINDOWS_RESERVED.test(cleaned) ? `_${cleaned}` : cleaned;
  const name = Array.from(named)
    // Truncate by code point so a surrogate pair is never split.
    .slice(0, MAX_LENGTH)
    .join('')
    .replace(/[. ]+$/, '');
  return name === '' || /^\.+$/.test(name) ? fallback : name;
}
