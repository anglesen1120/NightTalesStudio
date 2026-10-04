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
// policy over 8 000 characters (proxy header limit).
//
// Usage: node tools/web/gen-csp.mjs [--dir dist/web/browser] [--out dist/nginx/csp-headers.conf]

import { createHash } from 'node:crypto';
import { existsSync, mkdirSync, readdirSync, readFileSync, statSync, writeFileSync } from 'node:fs';
import { dirname, isAbsolute, join, relative, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const webRoot = resolve(dirname(fileURLToPath(import.meta.url)), '..', '..');

/** The contract value (README D4 `:128`, WR2.2): one add_header line, placeholder left literal. */
const MEDIA_PLACEHOLDER = '${NTS_CSP_MEDIA_ORIGINS}';
const WARN_POLICY_CHARS = 2_000;
export const MAX_POLICY_CHARS = 8_000;

/** Thrown for every condition that must exit 1 with file+line evidence (never a silent pass). */
export class CspError extends Error {}

const HTML_SUFFIX = /\.html?$/i;
const SKIP_DIRS = new Set(['node_modules', '.git', 'dist']);

function* walkHtml(dir) {
  for (const entry of readdirSync(dir, { withFileTypes: true }).sort((a, b) => a.name.localeCompare(b.name))) {
    const path = join(dir, entry.name);
    if (entry.isDirectory()) {
      if (!SKIP_DIRS.has(entry.name)) yield* walkHtml(path);
    } else if (entry.isFile() && HTML_SUFFIX.test(entry.name)) {
      yield path;
    }
  }
}

function lineOf(text, index) {
  let line = 1;
  for (let i = 0; i < index && i < text.length; i++) {
    if (text.charCodeAt(i) === 10) line++;
  }
  return line;
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

// Same-origin <script src>: relative URL or scheme-relative-less absolute path. Anything else fails.
function isSameOrigin(src) {
  const value = src.trim();
  if (/^[a-z][a-z0-9+.-]*:/i.test(value)) return false; // https:, http:, data:, javascript:, etc.
  if (value.startsWith('//')) return false; // protocol-relative = off-origin
  return true;
}

/** Attributes whose value the browser may navigate to or load. */
const URL_ATTRIBUTES = new Set(['href', 'src', 'action', 'formaction', 'xlink:href']);

const NAMED_ENTITIES = new Map([
  ['colon', ':'],
  ['tab', '\t'],
  ['newline', '\n'],
  ['amp', '&'],
  ['lt', '<'],
  ['gt', '>'],
  ['quot', '"'],
  ['apos', "'"],
]);

/** One pass of HTML character-reference decoding (numeric with/without `;`, plus the few named ones a URL needs). */
function decodeEntities(value) {
  return value.replace(/&(?:#(?:x([0-9a-f]+)|(\d+))|([a-z]+));?/gi, (whole, hex, decimal, named) => {
    if (named !== undefined) return NAMED_ENTITIES.get(named.toLowerCase()) ?? whole;
    const codePoint = Number.parseInt(hex ?? decimal, hex === undefined ? 10 : 16);
    return codePoint > 0 && codePoint <= 0x10ffff ? String.fromCodePoint(codePoint) : '\ufffd';
  });
}

/** A URL as the browser sees it: entities decoded, ASCII tab/newline removed, leading controls and spaces dropped. */
function normalizeUrl(value) {
  return decodeEntities(value)
    .replace(/[\t\n\r]/g, '')
    .replace(/^[\u0000-\u0020]+/, '');
}

/** A keyword attribute (`rel`, `http-equiv`): entities decoded, lower-cased, trimmed. */
function normalizeToken(value) {
  return decodeEntities(value).replace(/[\t\n\r]/g, ' ').trim().toLowerCase();
}

/** The attributes of one start tag: lower-cased name, raw value ('' when absent) and its offset in the tag. */
function parseAttributes(tagSource) {
  const attributes = [];
  const body = tagSource.replace(/^<[a-zA-Z][^\s>/]*/, (match) => ' '.repeat(match.length));
  for (const match of body.matchAll(/([^\s"'<>/=]+)(?:\s*=\s*(?:"([^"]*)"|'([^']*)'|([^\s"'=<>`]+)))?/g)) {
    attributes.push({ name: match[1].toLowerCase(), value: match[2] ?? match[3] ?? match[4] ?? '', index: match.index });
  }
  return attributes;
}

/** Analyses one HTML document. Returns { hashes } or throws CspError on a hard failure. */
export function analyseHtml(html, displayPath) {
  const fail = (what, index) => {
    throw new CspError(`${displayPath}:${lineOf(html, index)}: ${what} — ${snippet(html, index)}`);
  };
  const hashes = [];

  // 1. Inline event handlers: any on[a-z]+= attribute in the raw markup. Deliberately allow-listed
  //    names mirror tools/web/lint-forbidden-patterns.mjs so a build artefact that passes that scan
  //    also passes here.
  const NON_EVENT_ON = new Set(['ongoing', 'online', 'onboarding']);
  // Only start tags are scanned: script/style bodies are code, not attributes (a JS `once=` is not a handler).
  for (const tag of html.matchAll(/<[a-zA-Z][^\s>/]*(?:"[^"]*"|'[^']*'|[^'">])*>/g)) {
    for (const match of tag[0].matchAll(/(?:^|[\s"'/])(on[a-z]{3,})\s*=/gi)) {
      const name = match[1].toLowerCase();
      if (NON_EVENT_ON.has(name)) continue;
      fail(`inline event handler ${name}= is banned (CSP is hash-only; fix the template)`, tag.index + match.index);
    }
  }

  // 2. javascript: URLs in any URL-bearing attribute. The raw pass catches the plain spelling; the attribute pass
  //    below catches what the HTML parser would turn into one (entities, embedded tab/newline).
  for (const match of html.matchAll(/\b(?:href|src|action|formaction)\s*=\s*("|'|)\s*javascript\s*:/gi)) {
    fail('javascript: URL is banned (fix the template; there is no bypass flag)', match.index);
  }
  for (const tag of html.matchAll(/<[a-zA-Z][^\s>/]*(?:"[^"]*"|'[^']*'|[^'">])*>/g)) {
    const tagName = /^<([a-zA-Z][^\s>/]*)/.exec(tag[0])?.[1]?.toLowerCase() ?? '';
    const attributes = parseAttributes(tag[0]);
    const attr = (name) => attributes.find((candidate) => candidate.name === name);
    for (const { name, value, index } of attributes) {
      if (URL_ATTRIBUTES.has(name) && /^javascript:/i.test(normalizeUrl(value))) {
        fail('javascript: URL is banned (fix the template; there is no bypass flag)', tag.index + index);
      }
    }
    // 2b. A <link> that pulls a stylesheet or module graph from another origin (style-src / script-src would block
    //     it at runtime, but the build must say so before the image ships).
    if (tagName === 'link') {
      const rel = normalizeToken(attr('rel')?.value ?? '').split(/\s+/);
      const href = attr('href');
      if (href !== undefined && (rel.includes('stylesheet') || rel.includes('modulepreload'))) {
        const target = normalizeUrl(href.value);
        if (!isSameOrigin(target)) {
          fail(`external <link rel="${rel.join(' ')}" href="${target}"> is banned (self-host it)`, tag.index + href.index);
        }
      }
    }
    // 2c. <meta http-equiv="refresh" content="0;url=javascript:...">.
    if (tagName === 'meta' && normalizeToken(attr('http-equiv')?.value ?? '') === 'refresh') {
      const content = attr('content');
      if (content !== undefined && /javascript\s*:/i.test(normalizeUrl(content.value))) {
        fail('javascript: URL in <meta http-equiv="refresh"> is banned', tag.index + content.index);
      }
    }
  }

  // 3. Scripts: hash executable inline bodies; reject off-origin src.
  for (const match of html.matchAll(/<script\b([^>]*)>([\s\S]*?)<\/script\s*>/gi)) {
    const attrs = match[1];
    const src = /\bsrc\s*=\s*(?:"([^"]*)"|'([^']*)'|([^\s>]+))/i.exec(attrs);
    if (src !== null) {
      const value = src[1] ?? src[2] ?? src[3] ?? '';
      if (!isSameOrigin(value)) {
        fail(`external <script src="${value}"> is banned (self-host it; never widen script-src)`, match.index);
      }
      continue;
    }
    const type = /\btype\s*=\s*(?:"([^"]*)"|'([^']*)'|([^\s>]+))/i.exec(attrs);
    const typeValue = (type?.[1] ?? type?.[2] ?? type?.[3] ?? '').trim().toLowerCase();
    if (INERT_SCRIPT_TYPES.has(typeValue)) continue;
    const body = match[2];
    if (body.trim().length === 0) continue; // <script></script> carries nothing to allow
    const digest = createHash('sha256').update(body, 'utf8').digest('base64');
    hashes.push(`'sha256-${digest}'`);
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
