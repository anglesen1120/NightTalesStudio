import { defineConfig, devices } from '@playwright/test';

/**
 * Playwright suite (README D15). One project: Chromium (the Firefox half of the matrix is recorded manually in 10.1.1-T2).
 *
 * `NTS_E2E_TARGET`: `dev` (default) runs against `npm start`; `container` is the mode `e2e.yml` (11.5.1-T1) uses
 * against the compose `web` profile where nginx serves, and also asserts the D4 header matrix.
 * `NTS_E2E_BASE_URL` overrides the origin; locales are never hard-coded.
 */
export default defineConfig({
  testDir: './e2e',
  outputDir: 'e2e-results',
  // The refresh cookie rotates and TOTP codes are single-use per 30 s step, so specs share one owner: one worker.
  workers: 1,
  fullyParallel: false,
  forbidOnly: !!process.env['CI'],
  retries: 0,
  globalTeardown: './e2e/global-teardown.ts',
  reporter: [['list'], ['html', { open: 'never', outputFolder: 'playwright-report' }]],
  use: {
    baseURL: process.env['NTS_E2E_BASE_URL'] ?? 'http://localhost:4200',
    trace: 'retain-on-failure',
    video: 'retain-on-failure',
    screenshot: 'only-on-failure',
  },
  projects: [{ name: 'chromium', use: { ...devices['Desktop Chrome'] } }],
  webServer: {
    command: 'npm start',
    url: 'http://localhost:4200',
    // `e2e.yml` and a developer's running `npm start` / compose stack are reused as they are.
    reuseExistingServer: true,
    timeout: 120_000,
  },
});
