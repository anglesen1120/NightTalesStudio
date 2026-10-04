#!/usr/bin/env node
// Build-time legal content generator (task 10.3.1-T1, README D12): manifest.json + versioned Markdown files ->
// one committed, readonly TypeScript module of structured blocks. The Angular app never parses Markdown and never
// assigns innerHTML: every run is plain text, `emphasis` or a `link` whose href was restricted here.
//
//   manifest.json   { "privacy": { "version", "effectiveDate" }, "tos": { "version", "effectiveDate" } }
//                   manifest key -> file prefix / route doc: privacy -> privacy, tos -> terms (13 CR-34)
//   <prefix>.<version>.<lang>.md   lang in vi|en; both languages share the manifest version. A file of any other
//                   version in the directory is ignored; a missing one fails (version-skew guard).
//
// Markdown subset (everything else is a hard failure, never a silent drop):
//   `# Title` once, as the first non-blank line (plain text) | `## ` / `### ` headings | paragraphs (consecutive
//   lines joined with one space) | `- ` and `1. ` lists, one line per item (a blank line between items of the same
//   kind keeps one list) | `> ` quotes (consecutive lines joined) | inline `**emphasis**` and `[text](href)`.
//   href: `https://…` (valid URL, no credentials), `mailto:…`, or a site-relative `/…` (not `//`).
//
// Text rules: UTF-8 is validated strictly (TextDecoder fatal). CRLF is normalised to LF; any other control
// character (a lone CR and TAB included), DEL, C1, U+2028/2029, bidi controls (U+202A-202E, U+2066-2069) and
// U+FEFF (a BOM, anywhere) are rejected.
//
// Every rejection is `<file>:<line>: <reason>` on stderr and exit 1; the output file is written only after every
// document passed (temp file + rename), so a failed run never leaves a partial or stale-mixed file.
//
// Usage: node tools/web/legal-to-json.mjs [--fixtures <dir>] [--out <path>]
//        node tools/web/legal-to-json.mjs [--fixtures <dir>] --file <md>     (validate one Markdown file only)
// Exit codes: 0 ok | 1 content rejected | 2 usage error.

import { existsSync, mkdirSync, readFileSync, renameSync, rmSync, writeFileSync } from 'node:fs';
import { dirname, isAbsolute, join, relative, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const webRoot = resolve(dirname(fileURLToPath(import.meta.url)), '..', '..');
const DEFAULT_DIR = join(webRoot, 'src', 'content', 'legal');
const DEFAULT_OUT = join(webRoot, 'src', 'app', 'features', 'public', 'data-access', 'legal-content.generated.ts');

/** Manifest key -> route doc and file prefix (13 CR-34). Order = output order (LEGAL_DOCS). */
const MANIFEST_DOCS = [
  { key: 'privacy', doc: 'privacy', prefix: 'privacy' },
  { key: 'tos', doc: 'terms', prefix: 'terms' },
];
const LANGS = ['vi', 'en'];

/** Thrown for every condition that must exit 1 with file + line evidence. */
export class LegalError extends Error {
  constructor(file, line, reason) {
    super(`${file}:${line}: ${reason}`);
    this.file = file;
    this.line = line;
    this.reason = reason;
  }
}

const display = (path) => {
  const rel = relative(process.cwd(), path);
  return rel === '' || rel.startsWith('..') || isAbsolute(rel) ? path : rel;
};

// ---------------------------------------------------------------------------------------------------------------
// Bytes -> text
// ---------------------------------------------------------------------------------------------------------------

/** Offset of the first byte that is not well-formed UTF-8 (diagnostics only; the TextDecoder is the authority). */
function firstInvalidUtf8(bytes) {
  let i = 0;
  while (i < bytes.length) {
    const b = bytes[i];
    let need = 0;
    let min = 0x80;
    let max = 0xbf;
    if (b <= 0x7f) {
      i += 1;
      continue;
    } else if (b >= 0xc2 && b <= 0xdf) {
      need = 1;
    } else if (b >= 0xe0 && b <= 0xef) {
      need = 2;
      if (b === 0xe0) min = 0xa0;
      if (b === 0xed) max = 0x9f; // U+D800-DFFF (surrogates) are not encodable
    } else if (b >= 0xf0 && b <= 0xf4) {
      need = 3;
      if (b === 0xf0) min = 0x90;
      if (b === 0xf4) max = 0x8f;
    } else {
      return i;
    }
    for (let k = 1; k <= need; k += 1) {
      const next = bytes[i + k];
      const lo = k === 1 ? min : 0x80;
      const hi = k === 1 ? max : 0xbf;
      if (next === undefined || next < lo || next > hi) return i + k;
    }
    i += need + 1;
  }
  return -1;
}

const lineOfOffset = (bytes, offset) => {
  let line = 1;
  for (let i = 0; i < offset && i < bytes.length; i += 1) if (bytes[i] === 0x0a) line += 1;
  return line;
};

const FORBIDDEN_CHAR = /[\u0000-\u0009\u000b-\u001f\u007f-\u009f\u2028\u2029\u202a-\u202e\u2066-\u2069\ufeff]/u;
const charName = (ch) => `U+${ch.codePointAt(0).toString(16).toUpperCase().padStart(4, '0')}`;

/** Reads one Markdown file: strict UTF-8, CRLF -> LF, forbidden characters rejected. Returns its lines. */
function readLines(path) {
  const file = display(path);
  if (!existsSync(path)) throw new LegalError(file, 1, 'file not found');
  const bytes = readFileSync(path);
  let text;
  try {
    text = new TextDecoder('utf-8', { fatal: true, ignoreBOM: true }).decode(bytes);
  } catch {
    const offset = firstInvalidUtf8(bytes);
    throw new LegalError(file, offset < 0 ? 1 : lineOfOffset(bytes, offset), 'invalid UTF-8 byte sequence');
  }
  text = text.replace(/\r\n/g, '\n');
  const lines = text.split('\n');
  lines.forEach((line, index) => {
    const bad = FORBIDDEN_CHAR.exec(line);
    if (bad) {
      const kind = bad[0] === '\ufeff' ? 'byte order mark' : 'control or bidi character';
      throw new LegalError(file, index + 1, `${kind} ${charName(bad[0])} is not allowed`);
    }
  });
  if (text.trim() === '') throw new LegalError(file, 1, 'blank file');
  return lines;
}

// ---------------------------------------------------------------------------------------------------------------
// Inline: **emphasis**, [text](href)
// ---------------------------------------------------------------------------------------------------------------

/** Joined text of several source lines, remembering the source line of every character. */
function joinLines(items) {
  let text = '';
  const starts = [];
  items.forEach((item, index) => {
    if (index > 0) text += ' ';
    starts.push({ at: text.length, line: item.line });
    text += item.text;
  });
  const lineAt = (position) => {
    let line = starts[0].line;
    for (const start of starts) if (start.at <= position) line = start.line;
    return line;
  };
  return { text, lineAt };
}

const ENTITY = /&(?:#[0-9]+|#[xX][0-9a-fA-F]+|[A-Za-z][A-Za-z0-9]*);/;
const HTML_START = /<[A-Za-z/!?]/;

/** Per-line checks that need no block context; `line` is the source line number for the message. */
function checkLine(file, line, text) {
  const fail = (reason) => {
    throw new LegalError(file, line, reason);
  };
  if (HTML_START.test(text)) fail('raw HTML, comment or autolink is not allowed');
  if (text.includes('![')) fail('image is not supported');
  if (text.includes('[^')) fail('footnote is not supported');
  if (text.includes('|')) fail('table (pipe character) is not supported');
  if (text.includes('`')) fail('code span or fenced code (backtick) is not supported');
  if (text.includes('\\')) fail(text.endsWith('\\') ? 'hard line break (trailing backslash) is not supported' : 'backslash escape is not supported');
  if (ENTITY.test(text)) fail('HTML entity is not supported');
  if (/ {2,}$/.test(text)) fail('hard line break (trailing spaces) is not supported');
  if (/^\[[^\]]*\]:/.test(text.trim())) fail('link reference definition is not supported');
}

/** `_` that would open/close emphasis (CommonMark flanking) or any `__`; intra-word `_` is plain text. */
const UNDERSCORE_EMPHASIS = /__|(?<![\p{L}\p{N}])_(?=\S)|(?<=\S)_(?![\p{L}\p{N}])/u;

function checkHref(href, fail) {
  if (href === '') fail('link href is empty');
  const shown = href.length > 60 ? `${href.slice(0, 60)}…` : href;
  if (/[\s<>"'`\\]/.test(href)) fail(`link href '${shown}' contains whitespace or an illegal character`);
  if (href.includes('(')) fail('parenthesis inside a link href is not supported');
  if (href.startsWith('/')) {
    if (href.startsWith('//')) fail(`link href '${shown}' is protocol-relative (use https:// or a single leading /)`);
    return;
  }
  if (href.startsWith('https://')) {
    let url;
    try {
      url = new URL(href);
    } catch {
      fail(`link href '${shown}' is not a valid URL`);
    }
    if (url.protocol !== 'https:' || url.hostname === '' || !/^https:\/\/[^/?#]/.test(href)) fail(`link href '${shown}' is not a valid https URL`);
    if (url.username !== '' || url.password !== '') fail(`link href '${shown}' contains credentials`);
    return;
  }
  if (href.startsWith('mailto:')) {
    if (href.length === 'mailto:'.length) fail('mailto link has no address');
    return;
  }
  fail(`link scheme not allowed in '${shown}' (allowed: https://, mailto:, site-relative /)`);
}

/**
 * Parses already-joined inline text into runs. `fail(position, reason)` throws with the right source line.
 * No markup may nest: emphasis and link text are plain.
 */
function parseInline(joined, file, { plain = false } = {}) {
  const { text, lineAt } = joined;
  const fail = (position, reason) => {
    throw new LegalError(file, lineAt(Math.max(0, Math.min(position, text.length - 1))), reason);
  };
  const plainText = (segment, start) => {
    if (UNDERSCORE_EMPHASIS.test(segment)) fail(start, "'_' emphasis is not supported");
  };
  const runs = [];
  let buffer = '';
  let bufferStart = 0;
  const flush = () => {
    if (buffer !== '') {
      plainText(buffer, bufferStart);
      runs.push({ kind: 'text', text: buffer });
      buffer = '';
    }
  };
  let i = 0;
  while (i < text.length) {
    const c = text[i];
    if (c === '*') {
      if (plain) fail(i, 'inline markup is not allowed in the title');
      if (!text.startsWith('**', i)) fail(i, "'*' emphasis or bullet is not supported (use '**bold**' or '- ')");
      if (text[i + 2] === '*') fail(i, "'***' is not supported");
      const close = text.indexOf('**', i + 2);
      if (close < 0) fail(i, "unbalanced '**'");
      const inner = text.slice(i + 2, close);
      if (inner === '') fail(i, 'empty emphasis');
      if (/^\s|\s$/.test(inner)) fail(i, 'emphasis must not start or end with whitespace');
      if (/[*[\]]/.test(inner)) fail(i, 'markup nested inside emphasis is not supported');
      plainText(inner, i + 2);
      flush();
      runs.push({ kind: 'emphasis', text: inner });
      i = close + 2;
      bufferStart = i;
    } else if (c === '[') {
      if (plain) fail(i, 'inline markup is not allowed in the title');
      const close = text.indexOf(']', i + 1);
      if (close < 0) fail(i, "unbalanced '['");
      const label = text.slice(i + 1, close);
      if (/[[*]/.test(label)) fail(i, 'markup nested inside link text is not supported');
      if (label.trim() === '') fail(i, 'empty link text');
      if (text[close + 1] !== '(') fail(i, "reference link or unbalanced '[' (only [text](href) is supported)");
      const end = text.indexOf(')', close + 2);
      if (end < 0) fail(i, "unbalanced '(' in link");
      const href = text.slice(close + 2, end);
      checkHref(href, (reason) => fail(i, reason));
      plainText(label, i + 1);
      flush();
      runs.push({ kind: 'link', text: label, href });
      i = end + 1;
      bufferStart = i;
    } else if (c === ']') {
      fail(i, "unbalanced ']'");
    } else {
      if (buffer === '') bufferStart = i;
      buffer += c;
      i += 1;
    }
  }
  flush();
  return runs;
}

// ---------------------------------------------------------------------------------------------------------------
// Blocks
// ---------------------------------------------------------------------------------------------------------------

const HR = /^(?:(?:\*\s*){3,}|(?:-\s*){3,}|(?:_\s*){3,})$/;
const BLOCK_START = /^(?:#{1,6}(?:\s|$)|[-*+](?:\s|$)|\d+[.)](?:\s|$)|>|```|~~~)/;

/** Parses one Markdown file into `{ title, blocks }` (block ids are assigned by the caller). */
export function parseMarkdown(lines, file) {
  const fail = (line, reason) => {
    throw new LegalError(file, line, reason);
  };
  const blocks = [];
  let title = null;
  let paragraph = null; // { kind: 'paragraph'|'quote', items: [{ text, line }] }
  let list = null; // { ordered, items: [{ text, line }] , pendingBlank }
  let previousBlank = true;
  let previousWasItem = false;

  const closeParagraph = () => {
    if (!paragraph) return;
    const joined = joinLines(paragraph.items);
    blocks.push({ kind: paragraph.kind, runs: parseInline(joined, file) });
    paragraph = null;
  };
  const closeList = () => {
    if (!list) return;
    blocks.push({
      kind: 'list',
      ordered: list.ordered,
      items: list.items.map((item) => parseInline(joinLines([item]), file)),
    });
    list = null;
  };
  const closeAll = () => {
    closeParagraph();
    closeList();
  };

  lines.forEach((raw, index) => {
    const line = index + 1;
    if (raw.trim() === '') {
      closeParagraph(); // a list stays open: a following item of the same kind continues it
      if (list) list.pendingBlank = true;
      previousBlank = true;
      previousWasItem = false;
      return;
    }
    checkLine(file, line, raw);

    if (title === null) {
      const match = /^# (\S.*)$/.exec(raw);
      if (!match) fail(line, "first non-blank line must be exactly one '# Title'");
      const text = match[1].trim();
      if (/ #+$/.test(text)) fail(line, 'closing # sequence in a heading is not supported');
      const runs = parseInline(joinLines([{ text, line }]), file, { plain: true });
      title = runs.map((run) => run.text).join('');
      if (title === '') fail(line, 'title is empty');
      previousBlank = false;
      previousWasItem = false;
      return;
    }

    // Indentation: indented code, nested list or list continuation are all outside the subset.
    if (/^\s/.test(raw)) {
      if (previousWasItem) fail(line, 'nested list or list continuation line is not supported');
      fail(line, previousBlank && /^ {4,}/.test(raw) ? 'indented code block is not supported' : 'leading whitespace is not supported');
    }
    const text = raw.trimEnd();

    if (/^(```|~~~)/.test(text)) fail(line, 'fenced code block is not supported');
    if (/^=+$/.test(text)) fail(line, 'setext heading underline is not supported');
    if (/^-+$/.test(text) && paragraph && paragraph.kind === 'paragraph') fail(line, 'setext heading underline is not supported');
    if (HR.test(text)) fail(line, 'horizontal rule is not supported');

    const heading = /^(#{1,6})(?:\s+(.*))?$/.exec(text);
    if (heading) {
      const level = heading[1].length;
      if (level === 1) fail(line, 'a second H1 is not supported (the title is the only H1)');
      if (level > 3) fail(line, `heading level h${level} is not supported (only h2 and h3)`);
      const body = (heading[2] ?? '').trim();
      if (body === '') fail(line, 'heading is empty');
      if (/ #+$/.test(body)) fail(line, 'closing # sequence in a heading is not supported');
      closeAll();
      blocks.push({ kind: 'heading', level, runs: parseInline(joinLines([{ text: body, line }]), file) });
      previousBlank = false;
      previousWasItem = false;
      return;
    }

    if (text.startsWith('>')) {
      const body = text.replace(/^> ?/, '');
      if (body.trim() === '') fail(line, 'empty quote line is not supported');
      if (body.startsWith('>')) fail(line, 'nested quote is not supported');
      if (BLOCK_START.test(body)) fail(line, 'block construct inside a quote is not supported');
      closeList();
      if (!paragraph || paragraph.kind !== 'quote') {
        closeParagraph();
        paragraph = { kind: 'quote', items: [] };
      }
      paragraph.items.push({ text: body.trim(), line });
      previousBlank = false;
      previousWasItem = false;
      return;
    }

    const unordered = /^- (.*)$/.exec(text);
    const ordered = /^(\d{1,9})\. (.*)$/.exec(text);
    if (/^[*+](?:\s|$)/.test(text)) fail(line, "list marker is not supported (use '- ')");
    if (/^\d+\)(?:\s|$)/.test(text)) fail(line, "list marker is not supported (use '1. ')");
    if (text === '-' || (unordered && unordered[1].trim() === '') || (ordered && ordered[2].trim() === '')) fail(line, 'empty list item');
    if (unordered || ordered) {
      const isOrdered = Boolean(ordered);
      const body = (unordered ? unordered[1] : ordered[2]).trim();
      if (BLOCK_START.test(body)) fail(line, 'nested list or block construct inside a list item is not supported');
      closeParagraph();
      if (list && list.ordered !== isOrdered) closeList();
      if (!list) {
        if (isOrdered && ordered[1] !== '1') fail(line, "ordered list must start at '1.'");
        list = { ordered: isOrdered, items: [], pendingBlank: false };
      }
      list.pendingBlank = false;
      list.items.push({ text: body, line });
      previousBlank = false;
      previousWasItem = true;
      return;
    }

    // Plain paragraph text. A non-indented line right after a list item or a quote line would be a CommonMark
    // lazy continuation; the subset has none, so it is rejected instead of being read differently.
    if (previousWasItem) fail(line, 'list continuation line is not supported (one line per item)');
    if (paragraph && paragraph.kind === 'quote') fail(line, "quote continuation without '>' is not supported");
    closeList();
    if (!paragraph) paragraph = { kind: 'paragraph', items: [] };
    paragraph.items.push({ text: text.trim(), line });
    previousBlank = false;
    previousWasItem = false;
  });
  closeAll();
  return { title, blocks };
}

// ---------------------------------------------------------------------------------------------------------------
// Manifest
// ---------------------------------------------------------------------------------------------------------------

const SEMVER = /^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$/;

function isRealDate(value) {
  const match = /^(\d{4})-(\d{2})-(\d{2})$/.exec(value);
  if (!match) return false;
  const [year, month, day] = [Number(match[1]), Number(match[2]), Number(match[3])];
  const date = new Date(Date.UTC(year, month - 1, day));
  return date.getUTCFullYear() === year && date.getUTCMonth() === month - 1 && date.getUTCDate() === day;
}

export function readManifest(dir) {
  const path = join(dir, 'manifest.json');
  const file = display(path);
  const lines = readLines(path); // same strict text rules as the Markdown files
  const raw = lines.join('\n');
  const lineOfKey = (key) => {
    const index = lines.findIndex((line) => line.includes(`"${key}"`));
    return index < 0 ? 1 : index + 1;
  };
  let manifest;
  try {
    manifest = JSON.parse(raw);
  } catch (error) {
    throw new LegalError(file, 1, `manifest is not valid JSON (${error.message})`);
  }
  if (manifest === null || typeof manifest !== 'object' || Array.isArray(manifest)) {
    throw new LegalError(file, 1, 'manifest must be a JSON object keyed by document');
  }
  const known = new Set(MANIFEST_DOCS.map((entry) => entry.key));
  for (const key of Object.keys(manifest)) {
    if (!known.has(key)) throw new LegalError(file, lineOfKey(key), `unknown manifest key '${key}' (expected ${[...known].join(', ')})`);
  }
  return MANIFEST_DOCS.map((entry) => {
    const value = manifest[entry.key];
    if (value === undefined) throw new LegalError(file, 1, `missing manifest key '${entry.key}'`);
    const line = lineOfKey(entry.key);
    if (value === null || typeof value !== 'object' || Array.isArray(value)) throw new LegalError(file, line, `manifest key '${entry.key}' must be an object`);
    if (typeof value.version !== 'string' || !SEMVER.test(value.version)) {
      throw new LegalError(file, line, `manifest '${entry.key}'.version must be a x.y.z version string`);
    }
    if (typeof value.effectiveDate !== 'string' || !isRealDate(value.effectiveDate)) {
      throw new LegalError(file, line, `manifest '${entry.key}'.effectiveDate must be a real YYYY-MM-DD date`);
    }
    return { ...entry, version: value.version, effectiveDate: value.effectiveDate };
  });
}

// ---------------------------------------------------------------------------------------------------------------
// Conversion + output
// ---------------------------------------------------------------------------------------------------------------

/** Reads and converts every document/language; throws LegalError on the first problem. */
export function convert(dir) {
  const entries = readManifest(dir);
  const documents = [];
  for (const entry of entries) {
    for (const lang of LANGS) {
      const path = join(dir, `${entry.prefix}.${entry.version}.${lang}.md`);
      if (!existsSync(path)) {
        throw new LegalError(
          display(path),
          1,
          `missing legal content file for ${entry.doc}/${lang} (manifest.json says version ${entry.version})`,
        );
      }
      const { title, blocks } = parseMarkdown(readLines(path), display(path));
      documents.push({
        doc: entry.doc,
        lang,
        title,
        version: entry.version,
        effectiveDate: entry.effectiveDate,
        blocks: blocks.map((block, index) => ({ id: `block-${index + 1}`, ...block })),
      });
    }
  }
  return { entries, documents };
}

const quote = (value) => `'${value.replace(/\\/g, '\\\\').replace(/'/g, "\\'")}'`;
const runLiteral = (run) =>
  run.kind === 'link'
    ? `{ kind: 'link', text: ${quote(run.text)}, href: ${quote(run.href)} }`
    : `{ kind: '${run.kind}', text: ${quote(run.text)} }`;

function blockLiteral(block, indent) {
  const pad = ' '.repeat(indent);
  const inner = ' '.repeat(indent + 2);
  const out = [`${pad}{`, `${inner}id: ${quote(block.id)},`, `${inner}kind: ${quote(block.kind)},`];
  if (block.level !== undefined) out.push(`${inner}level: ${block.level},`);
  if (block.runs) {
    out.push(`${inner}runs: [`, ...block.runs.map((run) => `${inner}  ${runLiteral(run)},`), `${inner}],`);
  }
  if (block.items) {
    out.push(`${inner}items: [`);
    for (const item of block.items) {
      out.push(`${inner}  [`, ...item.map((run) => `${inner}    ${runLiteral(run)},`), `${inner}  ],`);
    }
    out.push(`${inner}],`);
  }
  if (block.ordered !== undefined) out.push(`${inner}ordered: ${block.ordered},`);
  out.push(`${pad}},`);
  return out;
}

/** The generated module text: deterministic, 2-space indent, single quotes, LF, final newline. */
export function render({ entries, documents }, sourceLabel) {
  const versions = entries.map((entry) => `${entry.key} ${entry.version}`).join(', ');
  const out = [
    `// GENERATED by tools/web/legal-to-json.mjs from ${sourceLabel} (${versions}). Do not edit.`,
    "import type { LegalDoc, LegalDocument, LegalLang } from '../domain/legal.model';",
    '',
    'export const LEGAL_CONTENT: Readonly<Record<LegalDoc, Readonly<Record<LegalLang, LegalDocument>>>> = {',
  ];
  for (const entry of entries) {
    out.push(`  ${entry.doc}: {`);
    for (const document of documents.filter((candidate) => candidate.doc === entry.doc)) {
      out.push(
        `    ${document.lang}: {`,
        `      doc: ${quote(document.doc)},`,
        `      lang: ${quote(document.lang)},`,
        `      title: ${quote(document.title)},`,
        `      version: ${quote(document.version)},`,
        `      effectiveDate: ${quote(document.effectiveDate)},`,
        '      blocks: [',
        ...document.blocks.flatMap((block) => blockLiteral(block, 8)),
        '      ],',
        '    },',
      );
    }
    out.push('  },');
  }
  out.push('};', '');
  return out.join('\n');
}

/** Writes via a temp file in the target directory, then renames: never a partial output. */
function writeAtomic(outPath, content) {
  mkdirSync(dirname(outPath), { recursive: true });
  const temp = `${outPath}.tmp-${process.pid}`;
  try {
    writeFileSync(temp, content);
    renameSync(temp, outPath);
  } finally {
    rmSync(temp, { force: true });
  }
}

export function generate(dir, outPath) {
  const result = convert(dir);
  const label = resolve(dir) === DEFAULT_DIR ? 'src/content/legal' : relative(webRoot, resolve(dir)).split('\\').join('/');
  writeAtomic(outPath, render(result, label));
  return { documents: result.documents.length, outFile: display(outPath) };
}

// ---------------------------------------------------------------------------------------------------------------
// CLI
// ---------------------------------------------------------------------------------------------------------------

const USAGE = 'usage: legal-to-json.mjs [--fixtures <dir>] [--out <path>] | [--fixtures <dir>] --file <md>';

function parseArgs(argv) {
  const options = {};
  for (let i = 0; i < argv.length; i += 1) {
    const name = argv[i];
    if (!['--fixtures', '--out', '--file'].includes(name)) return { error: `unknown argument '${name}'` };
    const value = argv[i + 1];
    if (value === undefined || value.startsWith('--')) return { error: `${name} needs a value` };
    options[name.slice(2)] = value;
    i += 1;
  }
  if (options.file !== undefined && options.out !== undefined) return { error: '--file validates one file and cannot be combined with --out' };
  return { options };
}

function main() {
  const { options, error } = parseArgs(process.argv.slice(2));
  if (error) {
    console.error(`legal:gen: ${error}\n${USAGE}`);
    process.exit(2);
  }
  const dir = options.fixtures ? resolve(options.fixtures) : DEFAULT_DIR;
  try {
    if (options.file !== undefined) {
      const path = isAbsolute(options.file) ? options.file : options.fixtures ? join(dir, options.file) : resolve(options.file);
      const { blocks } = parseMarkdown(readLines(path), display(path));
      console.log(`legal:gen PASS — ${display(path)}: ${blocks.length} block(s)`);
      return;
    }
    const result = generate(dir, options.out ? resolve(options.out) : DEFAULT_OUT);
    console.log(`legal:gen PASS — ${result.documents} document(s) -> ${result.outFile}`);
  } catch (caught) {
    if (caught instanceof LegalError) {
      console.error(caught.message);
      process.exit(1);
    }
    throw caught;
  }
}

if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  main();
}
