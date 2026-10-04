#!/usr/bin/env node
// Initial-chunk gzip budget (NFR-10-2, README D13): fails when the gzip sum of the files the fallback
// document loads up front exceeds the budget. `angular.json` budgets measure raw bytes; this measures the
// number the NFR is written in.
//
// Usage: node tools/web/check-gzip-budget.mjs --dir dist/web/browser [--budget 256000] [--html index.csr.html]

import { existsSync, readFileSync, statSync } from 'node:fs';
import { dirname, join, relative, resolve, sep } from 'node:path';
import { fileURLToPath } from 'node:url';
import { gzipSync } from 'node:zlib';

// README D13 reads "250 kB"; the unit is KiB, i.e. 256 000 bytes.
export const BUDGET_BYTES = 250 * 1024;

/** Fallback documents tried in order: `outputMode: static` emits `index.csr.html` (10.1.1-T1). */
const DEFAULT_HTML_CANDIDATES = ['index.csr.html', 'index.html'];

/** Thrown for every condition that must exit 1 with a message (never a silent pass). */
export class BudgetError extends Error {}

function attributeOf(tag, name) {
  const match = new RegExp(`\\s${name}\\s*=\\s*(?:"([^"]*)"|'([^']*)'|([^\\s>]+))`, 'i').exec(tag);
  return match ? (match[1] ?? match[2] ?? match[3]) : undefined;
}

/** Initial-chunk references of a document: `<script src>`, `<link rel="modulepreload">`, `<link rel="stylesheet">`. */
export function initialReferences(html) {
  const references = [];
  for (const [tag] of html.matchAll(/<script\b[^>]*>/gi)) {
    const src = attributeOf(tag, 'src');
    if (src !== undefined) {
      references.push(src);
    }
  }
  for (const [tag] of html.matchAll(/<link\b[^>]*>/gi)) {
    const rel = (attributeOf(tag, 'rel') ?? '').toLowerCase().split(/\s+/);
    const href = attributeOf(tag, 'href');
    if (href !== undefined && (rel.includes('modulepreload') || rel.includes('stylesheet'))) {
      references.push(href);
    }
  }
  return [...new Set(references)];
}

/**
 * Measures the initial chunks under `dir`.
 * @returns {{ html: string, rows: { file: string, raw: number, gzip: number }[], total: number, budget: number }}
 */
export function measure(dir, budget = BUDGET_BYTES, htmlName) {
  const root = resolve(dir);
  if (!existsSync(root) || !statSync(root).isDirectory()) {
    throw new BudgetError(`build output directory not found: ${dir} (run \`npm run build\` first)`);
  }

  const htmlFile = htmlName ?? DEFAULT_HTML_CANDIDATES.find((name) => existsSync(join(root, name)));
  if (htmlFile === undefined || !existsSync(join(root, htmlFile))) {
    throw new BudgetError(
      `fallback document not found in ${dir} (tried ${htmlName ?? DEFAULT_HTML_CANDIDATES.join(', ')})`,
    );
  }

  const references = initialReferences(readFileSync(join(root, htmlFile), 'utf8'));
  if (references.length === 0) {
    throw new BudgetError(`${htmlFile} references no initial script or stylesheet; refusing to report 0 bytes`);
  }

  const rows = references.map((reference) => {
    if (/^(?:[a-z][a-z0-9+.-]*:)?\/\//i.test(reference) || /^(?:data|blob):/i.test(reference)) {
      throw new BudgetError(`${htmlFile} loads ${reference}, which cannot be measured from the build output`);
    }
    const file = resolve(root, reference.split(/[?#]/)[0].replace(/^\/+/, ''));
    if (file !== root && !file.startsWith(root + sep)) {
      throw new BudgetError(`${reference} points outside ${dir}`);
    }
    if (!existsSync(file) || !statSync(file).isFile()) {
      throw new BudgetError(`${htmlFile} references ${reference}, but ${relative(root, file)} does not exist`);
    }
    const buffer = readFileSync(file);
    return {
      file: relative(root, file).split(sep).join('/'),
      raw: buffer.length,
      gzip: gzipSync(buffer, { level: 9 }).length,
    };
  });

  return { html: htmlFile, rows, total: rows.reduce((sum, row) => sum + row.gzip, 0), budget };
}

function parseArguments(argv) {
  const options = { dir: undefined, budget: BUDGET_BYTES, html: undefined };
  for (let index = 0; index < argv.length; index += 2) {
    const flag = argv[index];
    const value = argv[index + 1];
    if (value === undefined || !['--dir', '--budget', '--html'].includes(flag)) {
      throw new BudgetError(`usage: check-gzip-budget.mjs --dir <dir> [--budget <bytes>] [--html <file>] (got ${flag})`);
    }
    if (flag === '--dir') {
      options.dir = value;
    } else if (flag === '--html') {
      options.html = value;
    } else {
      options.budget = Number(value);
      if (!Number.isInteger(options.budget) || options.budget <= 0) {
        throw new BudgetError(`--budget must be a positive integer number of bytes, got ${value}`);
      }
    }
  }
  if (options.dir === undefined) {
    throw new BudgetError('--dir is required');
  }
  return options;
}

function main(argv) {
  try {
    const options = parseArguments(argv);
    const result = measure(options.dir, options.budget, options.html);
    console.log(`initial chunks of ${result.html}`);
    console.log('file raw gzip');
    for (const row of result.rows) {
      console.log(`${row.file} ${row.raw} ${row.gzip}`);
    }
    console.log(`total gzip ${result.total} bytes (budget ${result.budget})`);
    if (result.total > result.budget) {
      console.error(`FAIL: initial gzip total exceeds the budget by ${result.total - result.budget} bytes`);
      return 1;
    }
    console.log(`OK: ${result.budget - result.total} bytes under budget`);
    return 0;
  } catch (error) {
    if (error instanceof BudgetError) {
      console.error(`FAIL: ${error.message}`);
      return 1;
    }
    throw error;
  }
}

if (process.argv[1] && resolve(process.argv[1]) === resolve(dirname(fileURLToPath(import.meta.url)), 'check-gzip-budget.mjs')) {
  process.exitCode = main(process.argv.slice(2));
}
