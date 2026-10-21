import { test as base, expect, type BrowserContext, type ConsoleMessage, type Page } from '@playwright/test';

/**
 * CSP / Trusted Types violation detector (README D4, task 10.1.6-T1).
 *
 * EVERY e2e spec imports `test`/`expect` from here instead of `@playwright/test`, so the first page that renders
 * Material, opens the hub or pulls any third-party library is checked for a CSP or Trusted Types violation by
 * construction (the "Trusted Types breaks a library" risk is closed here, not by a manual checklist). A spec fails
 * in its `afterEach` phase when ANY page of its context recorded a violation.
 *
 * Detection (context-wide, installed before any page exists, attached to every current and future page,
 * including popups):
 *  - an init script listens for `securitypolicyviolation` and records the sanitized shape below on
 *    `window.__ntsCsp`, and mirrors it to a context binding so it survives navigations;
 *  - a console listener records only CSP / Trusted Types diagnostics, reduced to a fixed category;
 *  - `pageerror` is recorded only when it is a Trusted Types failure.
 * A record NEVER holds `blockedURI`, `sourceFile`, `sample`, a raw URL, query string, fragment, userinfo, request
 * body or page text, and neither does the failure message.
 *
 * Opting out: the deliberate-violation controls use `cspProbe` (`expectViolations: true`), which keeps the detector
 * running but does not auto-fail. Ordinary route specs never provoke a violation.
 */

/** The only fields ever kept about a violation. */
export interface CspRecord {
  source: 'event' | 'console' | 'pageerror';
  /** `securitypolicyviolation` fields (event) or a fixed category (console / pageerror). */
  violatedDirective: string;
  effectiveDirective: string;
  disposition: string;
  /** `location.pathname` only: no query, no fragment. */
  route: string;
  lineNumber: number;
}

declare global {
  interface Window {
    /** Sanitized violations of the current document (reset on navigation; the context binding keeps the history). */
    __ntsCsp?: CspRecord[];
    __ntsCspReport?: (record: CspRecord) => void;
    /** Trusted Types (not in lib.dom for this TypeScript): only what the e2e probes use. */
    trustedTypes?: {
      defaultPolicy: unknown;
      createPolicy(name: string, rules: { createHTML(input: string): string }): { createHTML(input: string): unknown };
    };
  }
}

const BINDING = '__ntsCspReport';

/** Runs in every document before any script. Must stay self-contained (serialised into the page). */
function installInPage(binding: string): void {
  const records: CspRecord[] = [];
  window.__ntsCsp = records;
  window.addEventListener(
    'securitypolicyviolation',
    (event) => {
      const record: CspRecord = {
        source: 'event',
        violatedDirective: String(event.violatedDirective).slice(0, 80),
        effectiveDirective: String(event.effectiveDirective).slice(0, 80),
        disposition: String(event.disposition),
        route: location.pathname,
        lineNumber: Number.isFinite(event.lineNumber) ? event.lineNumber : 0,
      };
      records.push(record);
      const report = Reflect.get(window, binding);
      if (typeof report === 'function') {
        report(record);
      }
    },
    true,
  );
}

const CSP_CONSOLE = /content security policy|refused to (?:execute|load|apply|connect|frame|create)|violates the following/i;
const TT_CONSOLE = /trusted ?type|trustedhtml|trustedscript|require-trusted-types|this document requires/i;

function consoleCategory(message: ConsoleMessage): string | null {
  if (message.type() !== 'error' && message.type() !== 'warning') {
    return null;
  }
  const text = message.text();
  if (TT_CONSOLE.test(text)) {
    return 'trusted-types';
  }
  return CSP_CONSOLE.test(text) ? 'csp' : null;
}

function routeOf(page: Page): string {
  try {
    return new URL(page.url()).pathname;
  } catch {
    return '';
  }
}

/**
 * Installs the detector on a context and returns its Node-side log (the full history, across navigations).
 * Call before the first page is created; pages that already exist are attached too.
 */
export async function installCspDetector(context: BrowserContext): Promise<CspRecord[]> {
  const log: CspRecord[] = [];
  await context.exposeBinding(BINDING, (_source, record: CspRecord) => {
    log.push({ ...record, source: 'event' });
  });
  await context.addInitScript(installInPage, BINDING);

  const attach = (page: Page): void => {
    page.on('console', (message) => {
      const category = consoleCategory(message);
      if (category !== null) {
        log.push({
          source: 'console',
          violatedDirective: category,
          effectiveDirective: category,
          disposition: 'enforce',
          route: routeOf(page),
          lineNumber: message.location().lineNumber,
        });
      }
    });
    page.on('pageerror', (error) => {
      if (TT_CONSOLE.test(error.message)) {
        log.push({
          source: 'pageerror',
          violatedDirective: 'trusted-types',
          effectiveDirective: 'trusted-types',
          disposition: 'enforce',
          route: routeOf(page),
          lineNumber: 0,
        });
      }
    });
  };
  context.pages().forEach(attach);
  context.on('page', attach);
  return log;
}

/** The current document's records from `window.__ntsCsp` (what the positive control asserts on). */
export async function pageCspRecords(page: Page): Promise<CspRecord[]> {
  return page.evaluate(() => window.__ntsCsp ?? []);
}

/** One line per violation, fixed fields only: nothing that could carry a token, cookie, URL or page text. */
export function describeCsp(records: readonly CspRecord[]): string {
  return records
    .map((r) => `${r.source} ${r.violatedDirective} (${r.disposition}) route=${r.route} line=${r.lineNumber}`)
    .join('\n');
}

interface CspFixtures {
  /** Node-side history of every violation recorded in this test's context. */
  cspLog: CspRecord[];
}
interface CspOptions {
  /** `true` only for the deliberate-violation controls: the detector runs but the test does not auto-fail. */
  expectViolations: boolean;
}

export const test = base.extend<CspFixtures & CspOptions>({
  expectViolations: [false, { option: true }],
  cspLog: [
    async ({ context, expectViolations }, use) => {
      const log = await installCspDetector(context);
      await use(log);
      // Let in-flight reports (dispatched after the last await of the test) reach the binding.
      await Promise.all(
        context.pages().map((page) =>
          page.evaluate(() => new Promise<void>((resolve) => setTimeout(resolve, 100))).catch(() => undefined),
        ),
      );
      if (!expectViolations) {
        expect(log, `CSP / Trusted Types violation(s) recorded:\n${describeCsp(log)}`).toEqual([]);
      }
    },
    { auto: true },
  ],
});

/** The isolated positive-control test: same detector, no auto-fail (see csp-negative.spec.ts). */
export const cspProbe = test.extend<object>({ expectViolations: true });

export { expect };
