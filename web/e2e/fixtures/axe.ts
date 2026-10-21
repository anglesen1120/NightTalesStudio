import { AxeBuilder } from '@axe-core/playwright';
import { expect, type Page } from '@playwright/test';
import type { Result } from 'axe-core';

/** WCAG 2.1 AA (README D16, D15). Wave 3 adds `wcag22aa` through its own signed upgrade (10 CR-23), not here. */
export const WCAG_TAGS = ['wcag2a', 'wcag2aa', 'wcag21a', 'wcag21aa'];

export interface A11yScope {
  /** CSS selectors to restrict the scan to. */
  include?: string[];
  /**
   * CSS selectors to leave out. A caller MUST add a comment naming the task that owns the excluded element
   * and a linked issue; a blanket disable is never acceptable.
   */
  exclude?: string[];
}

/** Runs axe with the WCAG tag set against the page as it is now and returns the violations. */
export async function collectA11yViolations(page: Page, { include, exclude }: A11yScope = {}): Promise<Result[]> {
  let builder = new AxeBuilder({ page }).withTags(WCAG_TAGS);
  for (const selector of include ?? []) {
    builder = builder.include(selector);
  }
  for (const selector of exclude ?? []) {
    builder = builder.exclude(selector);
  }
  return (await builder.analyze()).violations;
}

/** `id (impact): help` plus the first selectors of each failing node, one block per violation. */
export function describeViolations(violations: Result[]): string {
  return violations
    .map((violation) => {
      const nodes = violation.nodes.map((node) => `    - ${node.target.join(' ')}`).join('\n');
      return `${violation.id} (${violation.impact ?? 'n/a'}): ${violation.help}\n${nodes}`;
    })
    .join('\n');
}

/** Asserts the page has no axe violation; prints `id/impact/help/nodes` for every one that fails. */
export async function expectA11yClean(page: Page, scope: A11yScope = {}): Promise<void> {
  const violations = await collectA11yViolations(page, scope);
  if (violations.length > 0) {
    console.log(`axe violations on ${page.url()}:\n${describeViolations(violations)}`);
  }
  expect(violations, `axe violations:\n${describeViolations(violations)}`).toEqual([]);
}

/**
 * Self-proof of the axe setup: a page with an `<img>` without `alt` MUST be reported as `image-alt`.
 * A silently broken axe configuration (no rules loaded, wrong frame) would pass every page; this fails it.
 */
export async function expectA11yHarnessDetects(page: Page): Promise<void> {
  await page.setContent(
    '<!doctype html><html lang="en"><head><title>axe self-check</title></head><body><img src="x"></body></html>',
  );
  const violations = await collectA11yViolations(page);
  expect(
    violations.map((violation) => violation.id),
    'axe did not report the known-bad <img> without alt',
  ).toContain('image-alt');
}

/**
 * Waits until every CDK overlay (backdrop, dialog pane/container) has finished its enter animation and is fully
 * opaque. axe derives colour contrast from the currently rendered colours, so a scan taken mid-fade reports
 * contrast failures against the interpolated opacity (10.3.3-T3 D7). Callers keep the contrast rule ON: this only
 * waits for the real, settled rendering a user sees. Returns once no overlay animation is running and every
 * overlay surface reports `opacity: 1`.
 */
export async function waitForOverlayAnimationEnd(page: Page): Promise<void> {
  await page.waitForFunction(() => {
    const root = document.querySelector('.cdk-overlay-container');
    if (!root) return false;
    const surfaces = Array.from(
      root.querySelectorAll<HTMLElement>('.cdk-overlay-backdrop, .mat-mdc-dialog-container, [role="dialog"]'),
    );
    if (surfaces.length === 0) return false;
    const running = root.getAnimations({ subtree: true }).some((animation) => animation.playState === 'running');
    return !running && surfaces.every((surface) => Number(getComputedStyle(surface).opacity) === 1);
  });
}
