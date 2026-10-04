import { safeFilename } from './safe-filename';

/**
 * The only download path in the app (README D4): wave-2 download endpoints and any blob export go through here, so
 * no ad-hoc object URL or unsanitised `download` name exists. The object URL is revoked in a `finally`, so a
 * throwing click cannot leak it.
 */
export function downloadBlob(blob: Blob, filename: string): void {
  const url = URL.createObjectURL(blob);
  try {
    const anchor = document.createElement('a');
    anchor.href = url;
    anchor.download = safeFilename(filename);
    anchor.click();
  } finally {
    URL.revokeObjectURL(url);
  }
}
