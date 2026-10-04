import { TestBed } from '@angular/core/testing';
import { ZONELESS_TEST_PROVIDERS } from '../../testing/providers';
import { LegalBlock, NtsSanitizer, TRUSTED_TYPES, TrustedTypesFactory } from './nts-sanitizer';

/** A fake `window.trustedTypes`: records policy creation and wraps the output so we can tell it went through. */
function fakeFactory(): { factory: TrustedTypesFactory; created: string[] } {
  const created: string[] = [];
  const factory: TrustedTypesFactory = {
    createPolicy(name, rules) {
      created.push(name);
      return { createHTML: (input) => `TT:${rules.createHTML(input)}` };
    },
  };
  return { factory, created };
}

function sanitizerWith(factory: TrustedTypesFactory | null): NtsSanitizer {
  TestBed.resetTestingModule();
  TestBed.configureTestingModule({
    providers: [...ZONELESS_TEST_PROVIDERS, { provide: TRUSTED_TYPES, useValue: factory }],
  });
  return TestBed.inject(NtsSanitizer);
}

const render = (blocks: LegalBlock[]): string => String(sanitizerWith(fakeFactory().factory).toTrustedHtml(blocks));

describe('NtsSanitizer', () => {
  it('names the policy nts-sanitizer (the CSP trusted-types allow-list entry)', () => {
    expect(NtsSanitizer.POLICY_NAME).toBe('nts-sanitizer');
  });

  it('creates exactly one policy, lazily, and reuses it across calls and service instances', () => {
    const { factory, created } = fakeFactory();
    const first = sanitizerWith(factory);
    expect(created).toEqual([]);
    first.toTrustedHtml([{ kind: 'paragraph', text: 'a' }]);
    first.toTrustedHtml([{ kind: 'paragraph', text: 'b' }]);
    TestBed.resetTestingModule();
    sanitizerWith(factory).toTrustedHtml([{ kind: 'paragraph', text: 'c' }]);
    expect(created).toEqual(['nts-sanitizer']);
  });

  it('renders every block kind through a fixed template', () => {
    expect(
      render([
        { kind: 'heading', level: 3, text: 'Title' },
        { kind: 'paragraph', text: 'Body' },
        { kind: 'list', items: ['one', 'two'] },
        { kind: 'quote', text: 'Said' },
      ]),
    ).toBe('TT:<h3>Title</h3><p>Body</p><ul><li>one</li><li>two</li></ul><blockquote><p>Said</p></blockquote>');
  });

  it('escapes <script>, attribute-breaking quotes and <img onerror> inside text and items', () => {
    const out = render([
      { kind: 'paragraph', text: '<script>alert(1)</script>' },
      { kind: 'heading', text: '" onclick="alert(1)' },
      { kind: 'list', items: ['<img src=x onerror=alert(1)>', "it's & done"] },
      { kind: 'quote', text: "'><svg onload=alert(1)>" },
    ]);
    expect(out).not.toMatch(/<script|<img|<svg/);
    expect(out).toContain('&lt;script&gt;alert(1)&lt;/script&gt;');
    expect(out).toContain('&quot; onclick=&quot;alert(1)');
    expect(out).toContain('&lt;img src=x onerror=alert(1)&gt;');
    expect(out).toContain('it&#39;s &amp; done');
    // Every `<` that remains opens one of the five fixed tags.
    expect([...out.slice(3).matchAll(/<\/?([a-z0-9]+)/g)].every(([, tag]) => /^(h[1-6]|p|ul|li|blockquote)$/.test(tag ?? ''))).toBe(true);
  });

  it('drops a block kind outside the closed set', () => {
    const out = render([
      { kind: 'script', text: '<b>x</b>' } as unknown as LegalBlock,
      { kind: 'paragraph', text: 'kept' },
    ]);
    expect(out).toBe('TT:<p>kept</p>');
  });

  it('clamps a hostile heading level and tolerates missing fields', () => {
    expect(render([{ kind: 'heading', level: 99, text: 'a' }])).toBe('TT:<h6>a</h6>');
    expect(render([{ kind: 'heading', level: -4, text: 'a' }])).toBe('TT:<h1>a</h1>');
    expect(render([{ kind: 'heading', level: '1><script>' as unknown as number, text: 'a' }])).toBe('TT:<h2>a</h2>');
    expect(render([{ kind: 'paragraph' }, { kind: 'list' }])).toBe('TT:<p></p><ul></ul>');
  });

  it('exposes no HTML-string API: the class surface is toTrustedHtml(blocks) only', () => {
    expect(Object.getOwnPropertyNames(NtsSanitizer.prototype).sort()).toEqual(['constructor', 'toTrustedHtml']);
    expect(NtsSanitizer.prototype.toTrustedHtml.length).toBe(1);
    // A string where the block array belongs renders nothing instead of being iterated or passed through.
    expect(String(sanitizerWith(fakeFactory().factory).toTrustedHtml('<img src=x onerror=1>' as unknown as LegalBlock[]))).toBe('TT:');
  });

  it('the policy callback refuses any string the closed builder did not just produce', () => {
    let captured: { createHTML(input: string): string } | undefined;
    const factory: TrustedTypesFactory = {
      createPolicy(_name, rules) {
        captured = rules;
        return { createHTML: (input) => rules.createHTML(input) };
      },
    };
    sanitizerWith(factory).toTrustedHtml([{ kind: 'paragraph', text: 'x' }]);
    expect(() => captured?.createHTML('<img src=x onerror=alert(1)>')).toThrow(TypeError);
  });

  it('returns the same escaped markup as a plain string when the browser has no Trusted Types (jsdom)', () => {
    expect(sanitizerWith(null).toTrustedHtml([{ kind: 'paragraph', text: '<b>' }])).toBe('<p>&lt;b&gt;</p>');
  });
});
