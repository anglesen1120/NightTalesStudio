#!/usr/bin/env node
// Build-time hash CSP (README D4, WR2): runs after `ng build`, hashes every executable inline
// <script> in dist/web/browser, and writes ONE nginx include file — dist/nginx/csp-headers.conf —
// outside the served tree. The runtime stage installs it as
// /etc/nginx/templates/includes/csp-headers.conf.template and the image's envsubst fills the
// literal ${NTS_CSP_MEDIA_ORIGINS} at container start; this script never reads the env.
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

  // 2. javascript: URLs in any URL-bearing attribute.
  for (const match of html.matchAll(/\b(?:href|src|action|formaction)\s*=\s*("|'|)\s*javascript\s*:/gi)) {
    fail('javascript: URL is banned (fix the template; there is no bypass flag)', match.index);
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
    `img-src 'self' data: ${MEDIA_PLACEHOLDER}`,
    `media-src 'self' ${MEDIA_PLACEHOLDER}`,
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
  if ((imgSrc.split(MEDIA_PLACEHOLDER).length - 1) !== 1 || (mediaSrc.split(MEDIA_PLACEHOLDER).length - 1) !== 1) {
    throw new CspError(`${MEDIA_PLACEHOLDER} must occur literally exactly once per img-src/media-src directive`);
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
