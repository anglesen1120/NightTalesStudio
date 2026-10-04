#!/usr/bin/env node
// Build-time hash CSP (README D4, WR2): runs after `ng build`, hashes every executable inline
// <script> in dist/web/browser, and writes ONE nginx include file — dist/nginx/csp-headers.conf —
// outside the served tree. The runtime stage installs it as
// /etc/nginx/templates/includes/csp-headers.conf.template and the image's envsubst fills the
// literal ${NTS_CSP_MEDIA_ORIGINS} at container start (the value carries its own leading space, see
// docker-entrypoint.d/16-nts-media-origins.envsh); this script never reads the env.
//
// Hard failures (no bypass flag, by design): an inline on*= event handler, a javascript: URL in
// href/src/action/formaction, a <script src> that is not same-origin, a produced file with more
// than one add_header / unsafe-eval / unsafe-inline in script-src / a missing D4 directive, or a
// policy over 8 000 characters (proxy header limit). Every served *.html is parsed with the HTML5
// parser (parse5) and checked on real elements/attributes, never with regexes over raw markup.
//
// Usage: node tools/web/gen-csp.mjs [--dir dist/web/browser] [--out dist/nginx/csp-headers.conf]

import { createHash } from 'node:crypto';
import { existsSync, mkdirSync, readdirSync, readFileSync, statSync, writeFileSync } from 'node:fs';
import { dirname, isAbsolute, join, relative, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { parse } from 'parse5';

const webRoot = resolve(dirname(fileURLToPath(import.meta.url)), '..', '..');

/** The contract value (README D4 `:128`, WR2.2): one add_header line, placeholder left literal. */
const MEDIA_PLACEHOLDER = '${NTS_CSP_MEDIA_ORIGINS}';
const WARN_POLICY_CHARS = 2_000;
export const MAX_POLICY_CHARS = 8_000;

/** Thrown for every condition that must exit 1 with file+line evidence (never a silent pass). */
export class CspError extends Error {}

const HTML_SUFFIX = /\.html?$/i;
const SVG_NS = 'http://www.w3.org/2000/svg';

/**
 * Every *.html under the served root, whatever the directory is called: nginx serves `dist/`, `node_modules/` and
 * `.git/` folders in the built tree like any other, so none may be skipped.
 */
function* walkHtml(dir) {
  for (const entry of readdirSync(dir, { withFileTypes: true }).sort((a, b) => a.name.localeCompare(b.name))) {
    const path = join(dir, entry.name);
    if (entry.isDirectory()) {
      yield* walkHtml(path);
    } else if (entry.isFile() && HTML_SUFFIX.test(entry.name)) {
      yield path;
    }
  }
}

function snippet(text, index, span = 80) {
  return text.slice(Math.max(0, index - 8), index + span).replace(/\s+/g, ' ').trim().slice(0, 90);
}

/**
 * Executable non-JS MIME types a <script> may carry and still be inert (data blocks).
 * Only these are skipped; a missing or any other type is hashed as executable.
 */
const INERT_SCRIPT_TYPES = new Set([
  'application/json',
  'application/ld+json',
  'importmap',
  'speculationrules',
]);

/** ASCII whitespace as the HTML spec defines it (String.trim also strips U+00A0, U+FEFF, ...). */
const ASCII_WHITESPACE_EDGES = /^[\t\n\f\r ]+|[\t\n\f\r ]+$/g;

/**
 * Attribute values arrive entity-decoded and CRLF-normalised from the HTML parser; what is left to do is what
 * the URL parser does: drop leading C0 controls/spaces and remove ASCII tab/newline anywhere.
 */
function normalizeUrl(value) {
  return value.replace(/[\t\n\r]/g, '').replace(/^[\u0000-\u0020]+/, '');
}

/**
 * Same-origin URL: a relative reference without a scheme or authority. The browser reads `\` as `/` for
 * http(s) URLs, so `/\host`, `\/host` and `\\host` are protocol-relative (off-origin) just like `//host`.
 */
function isSameOrigin(url) {
  const value = normalizeUrl(url).replace(/\\/g, '/');
  if (/^[a-z][a-z0-9+.-]*:/i.test(value)) return false; // https:, http:, data:, javascript:, etc.
  return !value.startsWith('//');
}

/** Attributes whose value the browser may navigate to or load. */
const URL_ATTRIBUTES = new Set(['href', 'src', 'action', 'formaction', 'xlink:href']);

/** `on…` names that are not event handlers. Mirrors tools/web/lint-forbidden-patterns.mjs. */
const NON_EVENT_ON = new Set(['ongoing', 'online', 'onboarding']);

/** A keyword attribute (`rel`, `http-equiv`): tab/newline as space, trimmed, lower-cased. */
function normalizeToken(value) {
  return value.replace(/[\t\n\r]/g, ' ').trim().toLowerCase();
}

/** Every element of a parsed document in document order, including <template> content. */
function* elementsOf(root) {
  const stack = [root];
  while (stack.length > 0) {
    const node = stack.pop();
    if (node.tagName !== undefined) yield node;
    const children = [...(node.childNodes ?? [])];
    if (node.content !== undefined) children.push(node.content);
    for (let index = children.length - 1; index >= 0; index--) stack.push(children[index]);
  }
}

/** The real attributes of an element: qualified lower-case name (`xlink:href`) and decoded value. */
function attributesOf(element) {
  return element.attrs.map((attribute) => ({
    name: (attribute.prefix ? `${attribute.prefix}:${attribute.name}` : attribute.name).toLowerCase(),
    value: attribute.value,
  }));
}

/**
 * Analyses one HTML document with the same HTML5 parser the browser spec defines (parse5), so attribute
 * names, quoting, entities, raw-text bodies and newline normalisation are decided exactly once, by the
 * parser. Returns the inline-script hashes or throws CspError on a hard failure.
 */
export function analyseHtml(html, displayPath) {
  // scriptingEnabled:false parses <noscript> content as markup, so it is scanned too (the strict choice).
  const document = parse(html, { sourceCodeLocationInfo: true, scriptingEnabled: false });
  const fail = (what, element, attributeName) => {
    const location = element.sourceCodeLocation;
    const at = (attributeName === undefined ? undefined : location?.attrs?.[attributeName]) ?? location;
    throw new CspError(`${displayPath}:${at?.startLine ?? 1}: ${what} — ${snippet(html, at?.startOffset ?? 0)}`);
  };
  const hashes = [];

  for (const element of elementsOf(document)) {
    const attributes = attributesOf(element);
    const attr = (name) => attributes.find((candidate) => candidate.name === name);

    for (const { name, value } of attributes) {
      // 1. Inline event handlers: a real attribute named on<event>, never text inside a value, a comment or a
      //    script/style body.
      if (/^on[a-z]{3,}$/.test(name) && !NON_EVENT_ON.has(name)) {
        fail(`inline event handler ${name}= is banned (CSP is hash-only; fix the template)`, element, name);
      }
      // 2. javascript: URLs in any URL-bearing attribute (value already entity-decoded by the parser).
      if (URL_ATTRIBUTES.has(name) && /^javascript:/i.test(normalizeUrl(value))) {
        fail('javascript: URL is banned (fix the template; there is no bypass flag)', element, name);
      }
    }

    // 2b. A <link> that pulls a stylesheet or module graph from another origin (style-src / script-src would block
    //     it at runtime, but the build must say so before the image ships).
    if (element.tagName === 'link') {
      const rel = normalizeToken(attr('rel')?.value ?? '').split(/\s+/);
      const href = attr('href');
      if (href !== undefined && (rel.includes('stylesheet') || rel.includes('modulepreload'))) {
        const target = normalizeUrl(href.value);
        if (!isSameOrigin(target)) {
          fail(`external <link rel="${rel.join(' ')}" href="${target}"> is banned (self-host it)`, element, 'href');
        }
      }
    }

    // 2c. <meta http-equiv="refresh" content="0;url=javascript:...">.
    if (element.tagName === 'meta' && normalizeToken(attr('http-equiv')?.value ?? '') === 'refresh') {
      const content = attr('content');
      if (content !== undefined && /javascript\s*:/i.test(normalizeUrl(content.value))) {
        fail('javascript: URL in <meta http-equiv="refresh"> is banned', element, 'content');
      }
    }

    // 3. Scripts (HTML and SVG): reject off-origin src/href, hash executable inline bodies.
    if (element.tagName === 'script') {
      const source = element.namespaceURI === SVG_NS ? (attr('href') ?? attr('xlink:href')) : attr('src');
      if (source !== undefined) {
        const value = normalizeUrl(source.value);
        if (!isSameOrigin(value)) {
          fail(`external <script src="${value}"> is banned (self-host it; never widen script-src)`, element);
        }
        continue;
      }
      const type = (attr('type')?.value ?? '').replace(ASCII_WHITESPACE_EDGES, '').toLowerCase();
      if (INERT_SCRIPT_TYPES.has(type)) continue;
      // The text the browser executes (and hashes): parser-normalised newlines (CRLF/CR -> LF), not trimmed.
      const body = element.childNodes
        .filter((child) => child.nodeName === '#text')
        .map((child) => child.value)
        .join('');
      if (body.trim().length === 0) continue; // <script></script> carries nothing to allow
      const digest = createHash('sha256').update(body, 'utf8').digest('base64');
      hashes.push(`'sha256-${digest}'`);
    }
  }
  return hashes;
}

/** Builds the single policy value (the D4 string is the contract; placeholder stays literal). */
export function buildPolicy(sortedHashes) {
  const scriptSrc = ["script-src 'self'", ...sortedHashes].join(' ');
  return [
    "default-src 'none'",
    scriptSrc,
    "style-src 'self' 'unsafe-inline'",
    `img-src 'self' data:${MEDIA_PLACEHOLDER}`,
    `media-src 'self'${MEDIA_PLACEHOLDER}`,
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
  ].join('; ');
}

/** Produced-file guards (WR2.2): the emitted file itself must satisfy the contract. */
export function assertProducedFile(content) {
  const addHeaders = content.match(/add_header/g) ?? [];
  if (addHeaders.length !== 1) {
    throw new CspError(`produced file must hold exactly one add_header (found ${addHeaders.length})`);
  }
  const line = content.trimEnd();
  const value = /add_header\s+Content-Security-Policy\s+"([^"]+)"\s+always;/.exec(line)?.[1];
  if (value === undefined) {
    throw new CspError('produced file is not a single `add_header Content-Security-Policy "…" always;` line');
  }
  if (/unsafe-eval/.test(value)) {
    throw new CspError('produced policy must not contain unsafe-eval');
  }
  const scriptSrc = /(?:^|;\s*)script-src\s+([^;]+)/.exec(value)?.[1] ?? '';
  if (/unsafe-inline/.test(scriptSrc)) {
    throw new CspError('script-src must not contain unsafe-inline');
  }
  for (const directive of [
    "default-src 'none'",
    'script-src',
    'style-src',
    'img-src',
    'media-src',
    'font-src',
    'connect-src',
    'manifest-src',
    'worker-src',
    "object-src 'none'",
    "base-uri 'none'",
    "form-action 'self'",
    "frame-ancestors 'none'",
    "require-trusted-types-for 'script'",
    'trusted-types angular angular#bundler nts-sanitizer',
  ]) {
    if (!value.includes(directive)) {
      throw new CspError(`produced policy is missing the D4 directive: ${directive}`);
    }
  }
  const imgSrc = /(?:^|;\s*)img-src\s+([^;]+)/.exec(value)?.[1] ?? '';
  const mediaSrc = /(?:^|;\s*)media-src\s+([^;]+)/.exec(value)?.[1] ?? '';
  // The separator is part of the value (docker-entrypoint.d/16-nts-media-origins.envsh), so an empty list leaves
  // exactly `'self' data:` / `'self'`: the placeholder must sit directly after the last fixed token.
  if (imgSrc !== `'self' data:${MEDIA_PLACEHOLDER}` || mediaSrc !== `'self'${MEDIA_PLACEHOLDER}`) {
    throw new CspError(
      `${MEDIA_PLACEHOLDER} must occur literally exactly once per img-src/media-src directive, directly after ` +
        "`'self' data:` / `'self'` with no space (an empty value must leave no trailing whitespace)",
    );
  }
  if (value.length > MAX_POLICY_CHARS) {
    throw new CspError(
      `CSP value is ${value.length} characters (limit ${MAX_POLICY_CHARS}, proxy header limit); ` +
        'ship fewer inline scripts, never a shorter policy',
    );
  }
  return { value, warn: value.length > WARN_POLICY_CHARS };
}

/**
 * Generates dist/nginx/csp-headers.conf from the built HTML.
 * @returns {{ htmlFiles: number, hashes: string[], policy: string, outFile: string, warn: boolean }}
 */
export function generate(inputDir, outFile) {
  const root = resolve(webRoot, inputDir);
  if (!existsSync(root) || !statSync(root).isDirectory()) {
    throw new CspError(`build output directory not found: ${inputDir} (run \`npm run build\` first)`);
  }
  const hashSet = new Set();
  let htmlFiles = 0;
  for (const file of walkHtml(root)) {
    htmlFiles++;
    const displayPath = relative(webRoot, file).split('/').join('/');
    for (const hash of analyseHtml(readFileSync(file, 'utf8'), displayPath)) {
      hashSet.add(hash);
    }
  }
  const hashes = [...hashSet].sort();
  const policy = buildPolicy(hashes);
  const content = `add_header Content-Security-Policy "${policy}" always;\n`;
  const { warn } = assertProducedFile(content);

  const outPath = resolve(webRoot, outFile);
  // Produced-file placement guard: never inside the served tree (WR2.1), never inside the HTML root.
  const rel = relative(root, outPath);
  if (rel === '' || (!rel.startsWith('..') && !isAbsolute(rel))) {
    throw new CspError(`output ${outFile} would land inside the served tree ${inputDir} (WR2.1)`);
  }
  mkdirSync(dirname(outPath), { recursive: true });
  writeFileSync(outPath, content);
  return { htmlFiles, hashes, policy, outFile: relative(webRoot, outPath), warn };
}

function main() {
  const args = process.argv.slice(2);
  const option = (name, fallback) => {
    const index = args.indexOf(name);
    return index === -1 ? fallback : args[index + 1];
  };
  const dir = option('--dir', 'dist/web/browser');
  const out = option('--out', 'dist/nginx/csp-headers.conf');
  try {
    const result = generate(dir, out);
    const note = result.warn ? ` — WARNING: policy exceeds ${WARN_POLICY_CHARS} characters` : '';
    console.log(
      `csp:gen PASS — ${result.htmlFiles} HTML file(s), ${result.hashes.length} inline-script hash(es) -> ${result.outFile}${note}`,
    );
  } catch (error) {
    if (error instanceof CspError) {
      console.error(`csp:gen FAIL — ${error.message}`);
      process.exit(1);
    }
    throw error;
  }
}

if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  main();
}
