import { describe, expect, it } from 'vitest';
import { isSafeHttpUrl } from './research.model';

describe('isSafeHttpUrl (QA)', () => {
  const refused = [
    'javascript:alert(1)',
    'JavaScript:alert(1)',
    'data:text/html,<script>alert(1)</script>',
    'vbscript:msgbox(1)',
    '//evil.example/path',
    'ftp://files.example/a.txt',
    'file:///etc/passwd',
    'not a url',
    '',
  ];
  for (const value of refused) {
    it(`refuses ${JSON.stringify(value)}`, () => {
      expect(isSafeHttpUrl(value)).toBe(false);
    });
  }

  it('accepts https and http links', () => {
    expect(isSafeHttpUrl('https://news.example/a')).toBe(true);
    expect(isSafeHttpUrl('http://news.example/a')).toBe(true);
  });

  it('accepts an upper-case scheme because the URL parser lower-cases it', () => {
    expect(isSafeHttpUrl('HTTP://NEWS.EXAMPLE/A')).toBe(true);
  });
});
