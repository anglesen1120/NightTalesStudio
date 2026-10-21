import { expect, test } from './fixtures/csp.fixture';
import { msUntilNextStep, totp } from './fixtures/auth';

// RFC 6238 Appendix B, SHA-1, secret "12345678901234567890" (base32 below). Times are in seconds there;
// the helper takes milliseconds. The 8-digit values are the RFC's; the helper's default is 6 digits.
// No browser fixture is requested, so no browser launches.
// Public RFC 6238 Appendix B test key, not a credential.
const SECRET = 'GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ'; // gitleaks:allow

const VECTORS: { seconds: number; eightDigits: string }[] = [
  { seconds: 59, eightDigits: '94287082' },
  { seconds: 1111111109, eightDigits: '07081804' },
  { seconds: 1111111111, eightDigits: '14050471' },
  { seconds: 1234567890, eightDigits: '89005924' },
  { seconds: 2000000000, eightDigits: '69279037' },
];

test.describe('totp (RFC 6238, SHA-1)', () => {
  for (const { seconds, eightDigits } of VECTORS) {
    test(`t=${seconds}s -> ${eightDigits}`, () => {
      expect(totp(SECRET, { digits: 8, t: seconds * 1000 })).toBe(eightDigits);
      expect(totp(SECRET, { t: seconds * 1000 })).toBe(eightDigits.slice(-6));
    });
  }

  test('a code stays the same inside one 30 s step and changes at the boundary', () => {
    const boundary = 1111111080_000; // a multiple of 30 s
    expect(totp(SECRET, { t: boundary })).toBe(totp(SECRET, { t: boundary + 29_999 }));
    expect(totp(SECRET, { t: boundary })).not.toBe(totp(SECRET, { t: boundary + 30_000 }));
  });

  test('rejects a secret that is not base32', () => {
    expect(() => totp('not-base32-1!', { t: 0 })).toThrow('not valid base32');
  });

  test('waits past the next step boundary with 1.5 s of margin', () => {
    expect(msUntilNextStep(60_000)).toBe(31_500);
    expect(msUntilNextStep(89_999)).toBe(1_501);
  });
});
