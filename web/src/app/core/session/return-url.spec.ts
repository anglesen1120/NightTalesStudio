import { sanitizeReturnUrl } from './return-url';

describe('SanitizeReturnUrl', () => {
  it.each([
    '/dashboard',
    '/episodes/1?tab=story',
    '/series/3/episodes#segment-2',
    '/',
    '/loginfoo',
    '/login/extra',
    '/account/login',
  ])('accepts the in-app path %s unchanged', (url) => {
    expect(sanitizeReturnUrl(url)).toBe(url);
  });

  it.each([
    ['null', null],
    ['empty', ''],
    ['protocol-relative //evil', '//evil.example'],
    ['backslash /\\evil', '/\\evil.example'],
    ['absolute https', 'https://evil.example'],
    ['javascript scheme', 'javascript:alert(1)'],
    ['no leading slash', 'dashboard'],
    ['leading space', ' /dashboard'],
    ['embedded space', '/a b'],
    ['embedded tab', '/a\tb'],
    ['tab after slash', '/\t/evil.example'],
    ['newline after slash', '/\n/evil.example'],
    ['control character', '/\u0001/evil.example'],
    ['encoded protocol-relative', '/%2F%2Fevil.example'],
    ['encoded backslash', '/%5Cevil.example'],
    ['encoded tab', '/%09/evil.example'],
    ['malformed percent escape', '/%E0%A4%A'],
  ])('falls back to /dashboard for %s', (_name, url) => {
    expect(sanitizeReturnUrl(url)).toBe('/dashboard');
  });

  it.each([
    '/login',
    '/login/',
    '/LOGIN',
    '/Login?returnUrl=%2Fx',
    '/login#top',
    '/login;jsessionid=1',
    '/login%2F',
    '/forgot-password',
    '/reset-password?token=abc',
    '/verify-email/',
    '/Verify-Email#x',
  ])('refuses the auth page %s', (url) => {
    expect(sanitizeReturnUrl(url)).toBe('/dashboard');
  });
});
