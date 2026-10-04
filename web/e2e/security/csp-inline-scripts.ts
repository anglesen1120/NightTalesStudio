import { createHash } from 'node:crypto';

import type { Page } from '@playwright/test';

import { expect } from '../fixtures/csp.fixture';

/**
 * Executable inline <script> texts of the loaded document, exactly what tools/web/gen-csp.mjs hashes:
 * non-executable data blocks (INERT_SCRIPT_TYPES) and empty bodies are skipped, `script.text` is already
 * LF-normalised by the HTML parser.
 */
async function executableInlineScriptTexts(page: Page): Promise<string[]> {
  return page.evaluate(() => {
    const inert = ['application/json', 'application/ld+json', 'importmap', 'speculationrules'];
    return [...document.querySelectorAll<HTMLScriptElement>('script:not([src])')]
      .filter(
        (script) =>
          !inert.includes((script.getAttribute('type') ?? '').trim().toLowerCase()) &&
          script.text.trim() !== '',
      )
      .map((script) => script.text);
  });
}

/**
 * One directive of the policy, compared EXACTLY: only the `; ` separator's leading space is dropped, never trailing
 * whitespace (an empty `NTS_CSP_MEDIA_ORIGINS` must leave `img-src 'self' data:` with nothing after it).
 */
export function directive(policy: string, name: string): string | undefined {
  return policy
    .split(';')
    .map((part) => part.trimStart())
    .find((part) => part === name || part.startsWith(`${name} `));
}

/** The `'sha256-…'` source of an inline script as the browser hashes it (`script.text`, UTF-8). */
export function scriptDigest(text: string): string {
  return `'sha256-${createHash('sha256').update(text, 'utf8').digest('base64')}'`;
}

/**
 * Per-digest check (task 10.1.6-T1): every executable inline <script> of the CURRENT page must carry a
 * 'sha256-…' source in `scriptSrc`. Shared between csp.spec.ts (real pages) and csp-negative.spec.ts
 * (negative control). A page with zero inline scripts cannot prove the loop works, so it logs
 * '0 inline scripts checked' instead of passing vacuously — the negative control keeps the check honest.
 * No count comparison: the header holds one deduplicated set for the whole site.
 */
export async function expectInlineScriptsCoveredByCsp(
  page: Page,
  scriptSrc: string,
  label: string,
): Promise<number> {
  const inlineScriptTexts = await executableInlineScriptTexts(page);
  if (inlineScriptTexts.length === 0) {
    console.log(`${label}: 0 inline scripts checked`);
    return 0;
  }
  const headerHashes = new Set(scriptSrc.match(/'sha256-[A-Za-z0-9+/=]+'/g) ?? []);
  for (const text of inlineScriptTexts) {
    const digest = scriptDigest(text);
    expect(headerHashes.has(digest), `${label}: inline script ${digest} is not in the script-src hashes`).toBe(
      true,
    );
  }
  return inlineScriptTexts.length;
}
