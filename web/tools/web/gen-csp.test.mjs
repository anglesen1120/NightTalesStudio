import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { cpSync, existsSync, mkdirSync, mkdtempSync, readFileSync, readdirSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { dirname, join } from 'node:path';
import { after, describe, it } from 'node:test';
import { fileURLToPath } from 'node:url';
import { CspError, MAX_POLICY_CHARS, analyseHtml, assertProducedFile, buildPolicy, generate } from './gen-csp.mjs';

const HERE = dirname(fileURLToPath(import.meta.url));
const SCRIPT = join(HERE, 'gen-csp.mjs');
const FIXTURES = join(HERE, 'fixtures', 'csp');
const scratch = mkdtempSync(join(tmpdir(), 'nts-gen-csp-'));
after(() => rmSync(scratch, { recursive: true, force: true }));

/** Hand-computed with `printf 'window.__ntsBoot=1;' | openssl dgst -sha256 -binary | openssl base64 -A`. */
const KNOWN_HASH = "'sha256-w/rFRgCJf1C2JBttIGRb9Zi1NFUjMnTg37mRH+mz5N8='";

/** A scratch build tree holding the named fixtures as `browser/<name>.html`; returns { dir, out }. */
function tree(name, ...fixtures) {
  const base = join(scratch, name);
  const dir = join(base, 'dist', 'web', 'browser');
  mkdirSync(dir, { recursive: true });
  for (const fixture of fixtures) {
    cpSync(join(FIXTURES, fixture), join(dir, fixture));
  }
  return { base, dir, out: join(base, 'dist', 'nginx', 'csp-headers.conf') };
}

const policyOf = (file) => /add_header Content-Security-Policy "([^"]+)" always;/.exec(readFileSync(file, 'utf8'))[1];

function cli(...args) {
  const result = spawnSync(process.execPath, [SCRIPT, ...args], { encoding: 'utf8' });
  return { code: result.status, out: result.stdout, err: result.stderr };
}

describe('gen-csp: hashing', () => {
  it('hashes an inline script with the hand-computed SHA-256/base64', () => {
    const { dir, out } = tree('hash', 'clean.html');
    const result = generate(dir, out);
    assert.deepEqual(result.hashes, [KNOWN_HASH]);
    assert.match(policyOf(out), new RegExp(`script-src 'self' ${KNOWN_HASH.replace(/[+/]/g, '\\$&')};`));
  });

  it('does not hash a src script, and ignores JSON / importmap data blocks', () => {
    const { dir, out } = tree('json', 'json-script.html');
    const result = generate(dir, out);
    assert.deepEqual(result.hashes, []);
    assert.match(policyOf(out), /script-src 'self'; style-src/);
  });

  it('is deterministic: two runs are byte-identical and the hash set is sorted and de-duplicated', () => {
    const { dir, out } = tree('det', 'clean.html');
    cpSync(join(FIXTURES, 'clean.html'), join(dir, 'copy.html'));
    generate(dir, out);
    const first = readFileSync(out);
    generate(dir, out);
    assert.equal(Buffer.compare(first, readFileSync(out)), 0);
    assert.equal((policyOf(out).match(/sha256-/g) ?? []).length, 1);
  });

  it('walks nested directories (prerendered routes)', () => {
    const { dir, out } = tree('nested', 'clean.html');
    mkdirSync(join(dir, 'vi', 'privacy'), { recursive: true });
    writeFileSync(join(dir, 'vi', 'privacy', 'index.html'), '<html><body><script>var other=2;</script></body></html>');
    const result = generate(dir, out);
    assert.equal(result.htmlFiles, 2);
    assert.equal(result.hashes.length, 2);
  });
});

describe('gen-csp: hard failures (no bypass flag)', () => {
  it('fails on onclick= and names file + line + snippet', () => {
    const { dir, out } = tree('handler', 'inline-handler.html');
    assert.throws(() => generate(dir, out), (error) => {
      assert.ok(error instanceof CspError);
      assert.match(error.message, /inline-handler\.html:6: inline event handler onclick=/);
      assert.match(error.message, /alert\(1\)/);
      return true;
    });
    assert.equal(existsSync(out), false, 'nothing is written on failure');
  });

  it('fails on a javascript: URL', () => {
    const { dir, out } = tree('jsurl', 'javascript-url.html');
    assert.throws(() => generate(dir, out), /javascript-url\.html:5: javascript: URL is banned/);
  });

  it('fails on an external CDN <script src>', () => {
    const { dir, out } = tree('cdn', 'external-cdn.html');
    assert.throws(() => generate(dir, out), /external-cdn\.html:5: external <script src="https:\/\/cdn\.example\.com\/lib\.js"> is banned/);
  });

  it('fails on a protocol-relative <script src>', () => {
    assert.throws(() => analyseHtml('<script src="//evil.example/x.js"></script>', 'x.html'), /external <script src/);
  });

  it('fails on the injected critical-CSS onload handler (WR12: inlineCritical must stay false)', () => {
    const { dir, out } = tree('onload', 'critical-css-onload.html');
    assert.throws(() => generate(dir, out), /inline event handler onload=/);
  });

  it('does not treat code inside an inline script body as an attribute', () => {
    assert.deepEqual(analyseHtml('<script>const once=1;</script>', 'x.html').length, 1);
  });

  it('does not flag attribute names that merely start with "on"', () => {
    assert.deepEqual(analyseHtml('<div ongoing="x" online="y" onboarding="z"></div>', 'x.html'), []);
  });

  it('fails on a javascript: URL hidden behind an entity or an embedded tab/newline (fixture + variants)', () => {
    const { dir, out } = tree('jsurl-entity', 'javascript-url-entity.html');
    assert.throws(() => generate(dir, out), /javascript-url-entity\.html:5: javascript: URL is banned/);
    for (const href of [
      'java&#x09;script:alert(1)',
      'java&#9;script:alert(1)',
      'java&Tab;script:alert(1)',
      'java&NewLine;script:alert(1)',
      '&#106;avascript:alert(1)',
      '&#x6A;avascript:alert(1)',
      '&#106avascript:alert(1)',
      'javascript&colon;alert(1)',
      'java\tscript:alert(1)',
      'java\nscript:alert(1)',
      'java\r\nscript:alert(1)',
      '\u0001  javascript:alert(1)',
      'JaVaScRiPt:alert(1)',
    ]) {
      assert.throws(() => analyseHtml(`<a href="${href}">x</a>`, 'x.html'), /javascript: URL is banned/, JSON.stringify(href));
    }
    assert.throws(() => analyseHtml('<form action=&#106;avascript:alert(1)></form>', 'x.html'), /javascript: URL is banned/);
    assert.throws(() => analyseHtml('<button formaction="java&#x0A;script:1">x</button>', 'x.html'), /javascript: URL is banned/);
  });

  it('does not flag harmless look-alikes (a path, a query, a plain word, entity-escaped text)', () => {
    for (const href of ['/docs/javascript:intro', 'javascripts/app.js', '?q=javascript:', 'a&amp;b', '#top', '']) {
      assert.deepEqual(analyseHtml(`<a href="${href}">x</a>`, 'x.html'), [], JSON.stringify(href));
    }
  });

  it('fails on an external stylesheet or modulepreload <link> (fixture + variants), not on a same-origin one', () => {
    const { dir, out } = tree('cdn-css', 'external-stylesheet-link.html');
    assert.throws(() => generate(dir, out), /external-stylesheet-link\.html:4: external <link rel="stylesheet" href="https:\/\/cdn\.example\.com\/theme\.css"> is banned/);
    for (const link of [
      '<link rel="stylesheet" href="http://cdn.example.com/a.css">',
      '<link rel="modulepreload" href="https://cdn.example.com/a.js">',
      '<link href="//cdn.example.com/a.css" rel="stylesheet">',
      "<link rel='STYLESHEET' href='https://cdn.example.com/a.css'>",
      '<link rel="preload stylesheet" href="https://cdn.example.com/a.css">',
      '<link rel="stylesheet" href="https&colon;//cdn.example.com/a.css">',
      '<link rel=stylesheet href=https://cdn.example.com/a.css>',
    ]) {
      assert.throws(() => analyseHtml(link, 'x.html'), /external <link rel=/, link);
    }
    for (const link of [
      '<link rel="stylesheet" href="styles-ABC123.css">',
      '<link rel="modulepreload" href="/chunk-XYZ.js">',
      '<link rel="icon" href="https://cdn.example.com/favicon.ico">',
      '<link rel="preconnect" href="https://cdn.example.com">',
      '<link rel="canonical" href="https://example.com/">',
    ]) {
      assert.deepEqual(analyseHtml(link, 'x.html'), [], link);
    }
  });

  it('fails on <meta http-equiv=refresh> carrying a javascript: URL (fixture + variants), not on a same-origin refresh', () => {
    const { dir, out } = tree('meta-refresh', 'meta-refresh-javascript.html');
    assert.throws(() => generate(dir, out), /meta-refresh-javascript\.html:4: javascript: URL in <meta http-equiv="refresh"> is banned/);
    for (const meta of [
      '<meta http-equiv="REFRESH" content="0; URL=javascript:alert(1)">',
      '<meta http-equiv=refresh content="0;url=java&#x09;script:alert(1)">',
      "<meta content='0;url=&#106;avascript:alert(1)' http-equiv='refresh'>",
    ]) {
      assert.throws(() => analyseHtml(meta, 'x.html'), /javascript: URL in <meta/, meta);
    }
    for (const meta of [
      '<meta http-equiv="refresh" content="5;url=/login">',
      '<meta http-equiv="x-ua-compatible" content="javascript:">',
      '<meta name="description" content="about javascript: urls">',
    ]) {
      assert.deepEqual(analyseHtml(meta, 'x.html'), [], meta);
    }
  });
});

describe('gen-csp: produced file', () => {
  it('is written to dist/nginx/csp-headers.conf, never inside the served tree, and is the only file written', () => {
    const { base, dir, out } = tree('placement', 'clean.html');
    const before = readdirSync(dir);
    generate(dir, out);
    assert.deepEqual(readdirSync(dir), before, 'nothing under dist/web/browser changes');
    assert.deepEqual(readdirSync(join(base, 'dist', 'nginx')), ['csp-headers.conf']);
    assert.deepEqual(readdirSync(join(base, 'dist')).sort(), ['nginx', 'web']);
  });

  it('refuses an output path inside the served tree', () => {
    const { dir } = tree('inside', 'clean.html');
    assert.throws(() => generate(dir, join(dir, 'csp-headers.conf')), /would land inside the served tree/);
  });

  it('reads no environment variable: the output is identical for any NTS_CSP_MEDIA_ORIGINS', () => {
    assert.doesNotMatch(readFileSync(SCRIPT, 'utf8'), /process\.env/);
    const { dir, out } = tree('env', 'clean.html');
    const run = (env) => {
      const result = spawnSync(process.execPath, [SCRIPT, '--dir', dir, '--out', out], { env: { ...process.env, ...env } });
      assert.equal(result.status, 0);
      return readFileSync(out, 'utf8');
    };
    assert.equal(run({ NTS_CSP_MEDIA_ORIGINS: 'https://media.example.com' }), run({ NTS_CSP_MEDIA_ORIGINS: '' }));
  });

  it('holds exactly one add_header with every D4 directive, no unsafe-eval, no unsafe-inline outside style-src, and the placeholder literally once per directive', () => {
    const { dir, out } = tree('contract', 'clean.html');
    generate(dir, out);
    const content = readFileSync(out, 'utf8');
    assert.equal((content.match(/add_header/g) ?? []).length, 1);
    assert.ok(content.endsWith('" always;\n'));
    const policy = policyOf(out);
    for (const directive of [
      "default-src 'none'",
      "style-src 'self' 'unsafe-inline'",
      "img-src 'self' data:${NTS_CSP_MEDIA_ORIGINS}",
      "media-src 'self'${NTS_CSP_MEDIA_ORIGINS}",
      "font-src 'self'",
      "connect-src 'self'",
      "manifest-src 'self'",
      "worker-src 'self'",
      "object-src 'none'",
      "base-uri 'none'",
      "form-action 'self'",
      "frame-ancestors 'none'",
      "require-trusted-types-for 'script'",
      'trusted-types angular angular#bundler nts-sanitizer',
    ]) {
      assert.ok(policy.includes(directive), directive);
    }
    assert.equal((policy.match(/\$\{NTS_CSP_MEDIA_ORIGINS\}/g) ?? []).length, 2);
    assert.doesNotMatch(policy, /unsafe-eval/);
    const withoutStyle = policy.split('; ').filter((part) => !part.startsWith('style-src')).join('; ');
    assert.doesNotMatch(withoutStyle, /unsafe-inline/);
    assert.doesNotMatch(policy, /frame-src|report-uri|\*/);
  });

  it('assertProducedFile rejects a second add_header, unsafe-eval, script-src unsafe-inline and a missing directive', () => {
    const good = `add_header Content-Security-Policy "${buildPolicy([])}" always;\n`;
    assert.doesNotThrow(() => assertProducedFile(good));
    assert.throws(() => assertProducedFile(`${good}add_header X-Other "1";\n`), /exactly one add_header/);
    assert.throws(() => assertProducedFile(good.replace("script-src 'self'", "script-src 'self' 'unsafe-eval'")), /unsafe-eval/);
    assert.throws(() => assertProducedFile(good.replace("script-src 'self'", "script-src 'self' 'unsafe-inline'")), /unsafe-inline/);
    assert.throws(() => assertProducedFile(good.replace("require-trusted-types-for 'script'; ", '')), /require-trusted-types-for/);
    assert.throws(() => assertProducedFile(good.replace('${NTS_CSP_MEDIA_ORIGINS};', ';')), /exactly once per/);
    // The placeholder must sit directly after the last fixed token: a space before it would leave a trailing space
    // when the origin list is empty.
    assert.throws(() => assertProducedFile(good.replace("data:${", 'data: ${')), /no space/);
    assert.throws(() => assertProducedFile(good.replace("'self'${NTS_CSP_MEDIA_ORIGINS}; font", "'self' ${NTS_CSP_MEDIA_ORIGINS}; font")), /no space/);
  });

  it('renders to exactly `img-src \'self\' data:` and `media-src \'self\'` once the placeholder is an empty string', () => {
    const rendered = buildPolicy([]).replaceAll('${NTS_CSP_MEDIA_ORIGINS}', '');
    const parts = rendered.split('; ');
    assert.ok(parts.includes("img-src 'self' data:"), rendered);
    assert.ok(parts.includes("media-src 'self'"), rendered);
    assert.doesNotMatch(rendered, / ;| $/);
    const withOrigins = buildPolicy([]).replaceAll('${NTS_CSP_MEDIA_ORIGINS}', ' https://a.example https://b.example');
    assert.ok(withOrigins.split('; ').includes("img-src 'self' data: https://a.example https://b.example"));
    assert.ok(withOrigins.split('; ').includes("media-src 'self' https://a.example https://b.example"));
  });

  it(`trips the ${MAX_POLICY_CHARS}-character guard with a fixture of many inline scripts`, () => {
    const { dir, out } = tree('oversized');
    const scripts = Array.from({ length: 200 }, (_, index) => `<script>var n${index}=${index};</script>`).join('\n');
    writeFileSync(join(dir, 'many.html'), `<html><body>${scripts}</body></html>`);
    assert.throws(() => generate(dir, out), /CSP value is \d+ characters \(limit 8000/);
    assert.equal(existsSync(out), false);
  });

  it('fails loudly when the build output is missing', () => {
    assert.throws(() => generate(join(scratch, 'nope'), join(scratch, 'nope-out', 'x.conf')), /build output directory not found/);
  });
});

describe('gen-csp: CLI', () => {
  it('exits 0 and prints the summary for a clean tree', () => {
    const { dir, out } = tree('cli-ok', 'clean.html');
    const result = cli('--dir', dir, '--out', out);
    assert.equal(result.code, 0, result.err);
    assert.match(result.out, /csp:gen PASS — 1 HTML file\(s\), 1 inline-script hash\(es\)/);
  });

  it('exits 1 with csp:gen FAIL and file:line for an inline handler', () => {
    const { dir, out } = tree('cli-fail', 'inline-handler.html');
    const result = cli('--dir', dir, '--out', out);
    assert.equal(result.code, 1);
    assert.match(result.err, /csp:gen FAIL — .*inline-handler\.html:6/);
  });
});
