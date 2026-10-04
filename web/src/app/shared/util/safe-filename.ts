const MAX_LENGTH = 100;

/**
 * C0 + DEL + C1 controls, and the invisible format characters that spoof a name (zero-width marks, bidi overrides
 * such as U+202E, which turns "gpj.exe" into "exe.jpg" on screen). Dropped, not replaced: a replacement would still
 * reach the filesystem.
 */
function isControlOrSpoofing(codePoint: number): boolean {
  return (
    codePoint <= 0x1f ||
    (codePoint >= 0x7f && codePoint <= 0x9f) ||
    (codePoint >= 0x200b && codePoint <= 0x200f) ||
    (codePoint >= 0x202a && codePoint <= 0x202e) ||
    (codePoint >= 0x2066 && codePoint <= 0x2069) ||
    codePoint === 0xfeff
  );
}

/**
 * Makes a file name that came from the server (`Content-Disposition`, API field) safe to hand to `a.download`
 * (README D4). NFC -> drop quotes, `/`, `\` and `..` -> drop control characters -> collapse whitespace -> trim ->
 * truncate to 100 characters -> `fallback` when nothing but dots (or nothing) is left. Vietnamese diacritics are
 * kept and stay NFC.
 */
export function safeFilename(raw: string, fallback = 'download'): string {
  const visible = Array.from(String(raw).normalize('NFC'))
    .filter((character) => !isControlOrSpoofing(character.codePointAt(0) ?? 0))
    .join('');
  // Controls are dropped before the dot sweep so `.<NUL>.` collapses to `..` and is removed too.
  const name = Array.from(
    visible
      .replace(/["'`/\\]/g, '')
      .replace(/\.{2,}/g, '')
      .replace(/\s+/g, ' ')
      .trim(),
  )
    // Truncate by code point so a surrogate pair is never split.
    .slice(0, MAX_LENGTH)
    .join('')
    .trim();
  return name === '' || /^\.+$/.test(name) ? fallback : name;
}
