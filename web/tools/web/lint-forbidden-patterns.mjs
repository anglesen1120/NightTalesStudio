#!/usr/bin/env node
// Scans Angular templates for patterns the TypeScript lint rules cannot express (README D2/D4, task 10.1.2-T1):
// every *.html file, and the inline `template:` strings of every *.ts file.
// Prints `file:line: <pattern>` per hit and exits 1; exits 0 when clean.
//
// Usage: node tools/web/lint-forbidden-patterns.mjs [dir-or-file ...]   (default: src, relative to web/)
import { readdirSync, readFileSync, statSync } from 'node:fs';
import { dirname, extname, join, relative, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const webRoot = resolve(dirname(fileURLToPath(import.meta.url)), '..', '..');

const TEMPLATE_SUFFIXES = new Set(['.html', '.htm']);
const SCRIPT_SUFFIX = '.ts';
const SKIPPED_DIRS = new Set(['node_modules', 'dist', '.angular', '.git']);
// Generated files, excluded from lint like eslint.config.js `ignores`: the API client (10.1.4-T1, D5) and the legal
// content module (tools/web/legal-to-json.mjs, 10.3.1-T1).
const GENERATED = [
  resolve(webRoot, 'src/app/shared/api'),
  resolve(webRoot, 'src/app/features/public/data-access/legal-content.generated.ts'),
];

// Inline event-handler attributes are DENIED by shape (`on` + 3 or more letters + `=`) instead of allowed by a list of
// known event names (review F-4): the old allow-list missed events such as SVG SMIL `onbegin` / `onend` / `onrepeat`,
// `onbeforematch`, `oncontextlost`, `onscrollsnapchange`, `onshow` and `onwebkit*`, and needed upkeep for every new
// platform event. The cost is a few attribute names that merely start with `on`; these are the explicit exceptions
// (none of them is an event: the events are `ononline` / `onoffline`), so a custom input like `ongoing="..."` is not
// flagged. Any other `on<letters>=` attribute, including an Angular component's static `onSelect="..."` input,
// is rejected on purpose: the browser keeps it as an event-handler attribute on the host element.
const NON_EVENT_ON_ATTRIBUTES = ['ongoing', 'online', 'onboarding'];

// The `on*=` attribute must start a line or follow whitespace, a quote or `/` (`<img/onerror=...>`); Angular's own
// `on-click=` and `(click)=` syntaxes do not match.
const PATTERNS = [
  { name: '[innerHTML]', regex: /(?:\[|\bbind-)(?:attr\.)?inner-?html\b/i },
  { name: 'javascript:', regex: /javascript\s*:/i },
  {
    name: 'inline on*= handler',
    regex: new RegExp(`(?:^|[\\s"'/])(?!(?:${NON_EVENT_ON_ATTRIBUTES.join('|')})\\s*=)on[a-z]{3,}\\s*=`, 'i'),
  },
  { name: 'bypassSecurityTrust', regex: /bypassSecurityTrust/i },
  { name: 'srcdoc=', regex: /\ssrcdoc\s*=/i },
  { name: '<iframe', regex: /<iframe\b/i },
  { name: 'document.write', regex: /document\s*\.\s*write\b/ },
  { name: 'eval(', regex: /\beval\s*\(/ },
  { name: 'new Function', regex: /\bnew\s+Function\b/ },
];

// `template: \`...\`` / `'...'` / `"..."`; group 1 is the string literal including its quotes.
const INLINE_TEMPLATE = /\btemplate\s*:\s*(`(?:\\[\s\S]|[^`\\])*`|'(?:\\.|[^'\\\n])*'|"(?:\\.|[^"\\\n])*")/g;

function isScannable(file) {
  const extension = extname(file).toLowerCase();
  return TEMPLATE_SUFFIXES.has(extension) || extension === SCRIPT_SUFFIX;
}

function* walk(path) {
  const abs = resolve(path);
  if (GENERATED.some((generated) => abs === generated || abs.startsWith(generated + '/'))) {
    return;
  }
  const stat = statSync(abs);
  if (stat.isFile()) {
    if (isScannable(abs)) {
      yield abs;
    }
    return;
  }
  for (const entry of readdirSync(abs, { withFileTypes: true })) {
    if (entry.isDirectory() && SKIPPED_DIRS.has(entry.name)) {
      continue;
    }
    yield* walk(join(abs, entry.name));
  }
}

const targets = process.argv.slice(2);
const roots = (targets.length > 0 ? targets : ['src']).map((target) => resolve(webRoot, target));

// Yields `{ firstLine, text }` per template of a file: the whole file for .html, each inline `template:` string
// (first line number inside the .ts file) for .ts.
function* templatesOf(file) {
  const source = readFileSync(file, 'utf8');
  if (extname(file).toLowerCase() !== SCRIPT_SUFFIX) {
    yield { firstLine: 1, text: source };
    return;
  }
  for (const match of source.matchAll(INLINE_TEMPLATE)) {
    const bodyStart = match.index + match[0].length - match[1].length;
    yield { firstLine: source.slice(0, bodyStart).split('\n').length, text: match[1] };
  }
}

let templates = 0;
const hits = [];
for (const root of roots) {
  for (const file of walk(root)) {
    for (const { firstLine, text } of templatesOf(file)) {
      templates += 1;
      text.split(/\r?\n/).forEach((lineText, offset) => {
        for (const { name, regex } of PATTERNS) {
          if (regex.test(lineText)) {
            hits.push(`${relative(webRoot, file)}:${firstLine + offset}: ${name}`);
          }
        }
      });
    }
  }
}

if (hits.length > 0) {
  console.error(hits.join('\n'));
  console.error(`lint:forbidden FAIL — ${hits.length} forbidden pattern(s) in ${templates} template(s)`);
  process.exit(1);
}
console.log(`lint:forbidden PASS — ${templates} template(s) scanned, no forbidden patterns`);
