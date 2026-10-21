import { expect, test } from './fixtures/csp.fixture';
import { AUTH_SPEC_USE } from './fixtures/auth';

// No browser fixture is requested, so no browser launches (same convention as totp.unit.spec.ts).
test.describe('AUTH_SPEC_USE failure artifacts', () => {
  // SEC-1: the global config leaves `screenshot: 'only-on-failure'` active, so an auth spec that only
  // dropped trace/video could still persist the real TOTP manual key or recovery codes as a PNG under
  // e2e-results/ and playwright-report/. This pins all three capture channels off.
  test('disables trace, video and screenshot even on failure', () => {
    expect(AUTH_SPEC_USE).toEqual({ trace: 'off', video: 'off', screenshot: 'off' });
  });
});
