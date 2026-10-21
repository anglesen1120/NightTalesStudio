import { existsSync, readdirSync, rmSync, writeFileSync } from 'node:fs';
import { join, resolve } from 'node:path';
import { AUTH_DIR } from './fixtures/auth';

// `outputDir: 'e2e-results'` in playwright.config.ts resolves relative to the config dir (web/), which is
// `..` from this file — the same base AUTH_DIR uses.
const RESULTS_DIR = resolve(import.meta.dirname, '..', 'e2e-results');

/**
 * Removes the enrolled owner TOTP secret at the end of every run, failed or not (WR14). Prints nothing
 * about it.
 *
 * SEC-1 (10.3.3-T3 review): the per-spec `AUTH_SPEC_USE` turns trace, video and screenshot off, so no image,
 * recording or trace of the real manual key / recovery codes is persisted. Playwright 1.63.0 has one more
 * failure channel with no `use`-level switch and no per-spec opt-out: `error-context.md`, which embeds a page
 * snapshot (including the enrolment screen's visible secrets) and is attached from a result folder whose name
 * starts `<project>-<spec-file-slug>`. Rewriting that file (rather than deleting it) keeps the attachment
 * path valid for the HTML report, which reporters only build in `reporter.onEnd` — after this teardown.
 * The scrub is limited to folders of `auth-*.spec.ts` specs, i.e. the only specs allowed to touch the real
 * auth stack (`loginAsOwner`/`enrol`); every other result folder keeps its error context for debugging.
 * `playwright-report/` needs no cleanup: the html reporter removes and rebuilds that folder on every run
 * from the already-scrubbed result attachments.
 */
export default function globalTeardown(): void {
  rmSync(AUTH_DIR, { recursive: true, force: true });
  for (const dir of existsSync(RESULTS_DIR) ? readdirSync(RESULTS_DIR, { withFileTypes: true }) : []) {
    if (!dir.isDirectory() || !dir.name.includes('-auth-')) {
      continue;
    }
    const errorContext = join(RESULTS_DIR, dir.name, 'error-context.md');
    if (existsSync(errorContext)) {
      writeFileSync(errorContext, 'Page snapshot removed by global teardown: this auth spec may have captured enrolment secrets.\n');
    }
  }
}
