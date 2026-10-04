// Flat ESLint config (ESM) for the web workspace: README D2 layer boundaries, D2/D4/D6 security bans, D13 OnPush,
// D16 template accessibility. Task 10.1.2-T1.
//
// Flat-config rule options are REPLACED, not merged, when several blocks match one file. So every
// `no-restricted-imports` / `no-restricted-syntax` block below carries the COMPLETE list for its file class, and
// blocks are ordered general -> specific (the last matching block wins).
//
// Fixtures: every glob is also registered under `tools/lint-fixtures/*/src/` so the negative fixtures mirror the
// real tree (`<case>/src/app/features/series/domain/...`) and hit exactly the blocks the real files hit.
// The fixtures are in the global `ignores`; `tools/web/check-lint-fixtures.mjs` lints them with `--no-ignore`.
import { readdirSync } from 'node:fs';
import { dirname, join, resolve, sep } from 'node:path';
import { defineConfig } from 'eslint/config';
import angular from 'angular-eslint';
import tseslint from 'typescript-eslint';

// Wave-1 features (README D2). The list is the union of these static names and the directories that
// actually exist under src/app/features, so a new feature folder gets the cross-feature deep-import
// ban automatically (review F-6); the static entries keep the wave-1 features not yet on disk.
const STATIC_FEATURES = ['public', 'auth', 'dashboard', 'series', 'episodes'];
const featureDirs = (() => {
  try {
    return readdirSync(join(import.meta.dirname, 'src/app/features'), { withFileTypes: true })
      .filter((entry) => entry.isDirectory())
      .map((entry) => entry.name);
  } catch {
    return [];
  }
})();
const FEATURES = [...new Set([...STATIC_FEATURES, ...featureDirs])];

const ROOTS = ['src/', 'tools/lint-fixtures/*/src/'];
const at = (...suffixes) => ROOTS.flatMap((root) => suffixes.map((suffix) => root + suffix));

// ---------------------------------------------------------------------------------------------------------------
// Import boundaries (@typescript-eslint/no-restricted-imports)
// ---------------------------------------------------------------------------------------------------------------
const API = {
  group: ['@shared/api', '@shared/api/**'],
  message: 'only data-access imports the generated client (D2/D5)',
};
const DOMAIN = {
  group: [
    '@angular/**',
    'rxjs',
    'rxjs/**',
    '@core/**',
    '@features/**',
    '@shared/api',
    '@shared/api/**',
    '@shared/ui',
    '@shared/ui/**',
    '../data-access/**',
    '../application/**',
    '../ui/**',
  ],
  message: 'domain imports only its own files and @shared/util (D2)',
};
// `@shared/api` is already reported by API wherever both apply, so it is not repeated here (one report per import).
const UI = {
  group: [
    '@angular/common/http',
    '@angular/common/http/**',
    // Bare layer directories (a barrel index.ts) as well as deep paths: `ignore` does not match `x/**` against `x`.
    '**/data-access',
    '**/data-access/**',
    '**/application',
    '**/application/**',
  ],
  message: 'presentational ui takes inputs/outputs only (D2)',
};
const crossFeature = (feature) => ({
  group: FEATURES.filter((other) => other !== feature).map((other) => `@features/${other}/**`),
  message: 'a feature imports another only through its index.ts (D2)',
});

const imports = (...patterns) => ['error', { patterns }];

const importBlocks = [
  // Everything under src: no generated client ...
  { files: at('**/*.ts'), rules: { '@typescript-eslint/no-restricted-imports': imports(API) } },
  // ... except data-access (features/*/data-access and core/**/data-access, D5).
  { files: at('app/**/data-access/**/*.ts'), rules: { '@typescript-eslint/no-restricted-imports': 'off' } },
  { files: at('app/shared/ui/**/*.ts'), rules: { '@typescript-eslint/no-restricted-imports': imports(API, UI) } },
  { files: at('app/features/*/domain/**/*.ts'), rules: { '@typescript-eslint/no-restricted-imports': imports(DOMAIN) } },
  { files: at('app/features/*/ui/**/*.ts'), rules: { '@typescript-eslint/no-restricted-imports': imports(API, UI) } },
  // Per-feature blocks add the cross-feature rule on top of the layer lists above (and override them, so repeat them).
  ...FEATURES.flatMap((feature) => {
    const base = `app/features/${feature}`;
    const cross = crossFeature(feature);
    return [
      { files: at(`${base}/**/*.ts`), rules: { '@typescript-eslint/no-restricted-imports': imports(API, cross) } },
      { files: at(`${base}/data-access/**/*.ts`), rules: { '@typescript-eslint/no-restricted-imports': imports(cross) } },
      { files: at(`${base}/domain/**/*.ts`), rules: { '@typescript-eslint/no-restricted-imports': imports(DOMAIN) } },
      { files: at(`${base}/ui/**/*.ts`), rules: { '@typescript-eslint/no-restricted-imports': imports(API, UI, cross) } },
    ];
  }),
];

// ---------------------------------------------------------------------------------------------------------------
// Resolved-path boundaries: relative specifiers, dynamic import(), `import('x')` types
// ---------------------------------------------------------------------------------------------------------------
// The pattern lists above match the literal specifier, so `../../../shared/api/...` or `../../episodes/ui/...`
// would slip past `@shared/api` / `@features/episodes/**`, and the stock rule never visits `import('x')`.
// This wrapper re-checks every specifier in its tsconfig-alias form: a relative specifier is resolved against the
// importing file and rewritten to `@core|@shared|@features/...` when it lands in src/app; the rewritten value goes
// through the unchanged rule (same options, same ruleId, one report per import). A relative import that stays
// inside the importer's own unit (`features/<f>/<layer>`, `core/<x>`, `shared/<x>`) is left as written, so
// `./sibling` and `../domain/x` inside one layer are not treated as cross-layer imports.
const appPath = (file) => {
  const posix = file.split(sep).join('/');
  const index = posix.lastIndexOf('/src/app/');
  return index < 0 ? null : posix.slice(index + '/src/app/'.length);
};
const unitOf = (rel) => rel?.match(/^(core\/[^/]+|shared\/[^/]+|features\/[^/]+\/[^/]+)(?:\/|$)/)?.[1] ?? null;
const canonicalSpecifier = (specifier, filename) => {
  if (!specifier.startsWith('.')) {
    return specifier;
  }
  const target = appPath(resolve(dirname(filename), specifier));
  const match = target?.match(/^(core|shared|features)\/(.+)$/);
  if (!match) {
    return specifier;
  }
  const importerUnit = unitOf(appPath(filename));
  if (importerUnit !== null && unitOf(target) === importerUnit) {
    return specifier;
  }
  return `@${match[1]}/${match[2]}`;
};
// Static string of `'x'` or a substitution-free template literal; null for anything computed.
const staticString = (node) => {
  if (node?.type === 'Literal' && typeof node.value === 'string') {
    return node.value;
  }
  if (node?.type === 'TemplateLiteral' && node.expressions.length === 0) {
    return node.quasis[0].value.cooked;
  }
  return null;
};
const synthesizeImport = (node, specifier) => ({
  type: 'ImportDeclaration',
  importKind: 'value',
  specifiers: [],
  attributes: [],
  source: { type: 'Literal', value: specifier, raw: JSON.stringify(specifier), loc: node.loc, range: node.range },
  loc: node.loc,
  range: node.range,
  parent: node.parent,
});
const withResolvedPaths = (base) => ({
  ...base,
  create(context) {
    const handlers = base.create(context);
    const canonical = (specifier) => canonicalSpecifier(specifier, context.filename);
    const wrapped = {};
    for (const key of ['ImportDeclaration', 'ExportAllDeclaration', 'ExportNamedDeclaration[source]']) {
      if (handlers[key]) {
        wrapped[key] = (node) =>
          handlers[key]({ ...node, source: { ...node.source, value: canonical(node.source.value) } });
      }
    }
    if (handlers.TSImportEqualsDeclaration) {
      wrapped.TSImportEqualsDeclaration = (node) => {
        const reference = node.moduleReference;
        if (reference.type === 'TSExternalModuleReference') {
          const expression = { ...reference.expression, value: canonical(reference.expression.value) };
          handlers.TSImportEqualsDeclaration({ ...node, moduleReference: { ...reference, expression } });
        }
      };
    }
    if (handlers.ImportDeclaration) {
      const check = (node, specifier) => {
        if (specifier !== null) {
          handlers.ImportDeclaration(synthesizeImport(node, canonical(specifier)));
        }
      };
      wrapped.ImportExpression = (node) => check(node, staticString(node.source));
      wrapped.TSImportType = (node) =>
        check(node, node.argument.type === 'TSLiteralType' ? staticString(node.argument.literal) : null);
    }
    return wrapped;
  },
});
// Flat config forbids registering a second '@typescript-eslint' plugin object, so the rule is replaced in place
// (every config block that enables '@typescript-eslint/no-restricted-imports' then gets the wrapped version).
//
// The wrapper depends on the internals of a rule that typescript-eslint has deprecated (8.64.0, replaced by the
// core rule): its existence and the handler keys it returns. Fail at config load, naming the cause, instead of
// `base.create is not a function` on the first linted file or a silently unwrapped handler (review F-5).
const RESTRICTED_IMPORTS_HANDLERS = [
  'ImportDeclaration',
  'ExportAllDeclaration',
  'ExportNamedDeclaration[source]',
  'TSImportEqualsDeclaration',
];
const assertRestrictedImportsRule = (rule) => {
  const fail = (reason) => {
    throw new Error(
      `web/eslint.config.js: ${reason}. This file wraps '@typescript-eslint/no-restricted-imports' ` +
        `(typescript-eslint, exact-pinned in web/package.json) to resolve relative and dynamic imports; ` +
        `re-check the wrapper against the installed typescript-eslint before upgrading.`,
    );
  };
  if (typeof rule?.create !== 'function') {
    fail("tseslint.plugin.rules['no-restricted-imports'] is missing or has no create()");
  }
  const probe = rule.create({
    options: [{ patterns: [{ group: ['probe'] }] }],
    sourceCode: {},
    filename: 'probe.ts',
    report() {},
  });
  const missing = RESTRICTED_IMPORTS_HANDLERS.filter((key) => typeof probe[key] !== 'function');
  if (missing.length > 0) {
    fail(`'@typescript-eslint/no-restricted-imports' no longer exposes the handler(s) ${missing.join(', ')}`);
  }
};
assertRestrictedImportsRule(tseslint.plugin.rules['no-restricted-imports']);
tseslint.plugin.rules['no-restricted-imports'] = withResolvedPaths(tseslint.plugin.rules['no-restricted-imports']);

// ---------------------------------------------------------------------------------------------------------------
// Syntax bans (no-restricted-syntax)
// ---------------------------------------------------------------------------------------------------------------
const SECURITY = [
  {
    selector: "MemberExpression[property.name=/^bypassSecurityTrust/]",
    message: 'bypassSecurityTrust* is banned (D2/D4); sanitise through the Angular sanitizer or core/security',
  },
  {
    selector: "MemberExpression[computed=true][property.value=/^bypassSecurityTrust/]",
    message: 'bypassSecurityTrust* is banned (D2/D4); sanitise through the Angular sanitizer or core/security',
  },
  {
    selector: "MemberExpression[computed=true][property.quasis.0.value.cooked=/^bypassSecurityTrust/]",
    message: 'bypassSecurityTrust* is banned (D2/D4); sanitise through the Angular sanitizer or core/security',
  },
  {
    selector: "ObjectPattern > Property:matches([key.name=/^bypassSecurityTrust/], [key.value=/^bypassSecurityTrust/])",
    message: 'bypassSecurityTrust* is banned (D2/D4); do not destructure it out of the sanitizer',
  },
  {
    selector: 'Identifier[name=/^(eval|Function)$/]',
    message: 'eval / new Function are banned (D2/D4)',
  },
  {
    // globalThis['eval'](...), window['Function']('...'), window[`eval`](...): computed access skips the Identifier ban
    selector:
      'MemberExpression[computed=true]:matches([property.value=/^(eval|Function)$/], [property.quasis.0.value.cooked=/^(eval|Function)$/])',
    message: 'eval / new Function are banned (D2/D4)',
  },
  // D4: no HTML sink that skips the Angular sanitizer. `[innerHTML]` in templates is covered by the template scan.
  {
    selector:
      'AssignmentExpression[left.type="MemberExpression"]:matches([left.property.name=/^(innerHTML|outerHTML)$/], [left.property.value=/^(innerHTML|outerHTML)$/], [left.property.quasis.0.value.cooked=/^(innerHTML|outerHTML)$/])',
    message: 'innerHTML / outerHTML assignment skips the Angular sanitizer and is banned (D2/D4)',
  },
  {
    selector:
      'CallExpression[callee.type="MemberExpression"]:matches([callee.property.name=/^(insertAdjacentHTML|createContextualFragment)$/], [callee.property.value=/^(insertAdjacentHTML|createContextualFragment)$/], [callee.property.quasis.0.value.cooked=/^(insertAdjacentHTML|createContextualFragment)$/])',
    message: 'insertAdjacentHTML / createContextualFragment skip the Angular sanitizer and are banned (D2/D4)',
  },
  {
    // renderer.setProperty(el, 'innerHTML', html)
    selector:
      'CallExpression[callee.property.name="setProperty"]:matches([arguments.1.value=/^(innerHTML|outerHTML)$/], [arguments.1.quasis.0.value.cooked=/^(innerHTML|outerHTML)$/])',
    message: 'Renderer2.setProperty of innerHTML / outerHTML skips the Angular sanitizer and is banned (D2/D4)',
  },
  {
    // document.write(...), window.document.write(...), globalThis.document['write'](...), document['writeln'](...)
    selector:
      "MemberExpression:matches([object.name='document'], [object.property.name='document'], [object.property.value='document']):matches([computed=false][property.name=/^write(ln)?$/], [computed=true][property.value=/^write(ln)?$/])",
    message: 'document.write is banned (D2/D4)',
  },
];
// Storage outside core/prefs: localStorage, sessionStorage, indexedDB and document.cookie (D2/D6).
const STORAGE_MESSAGE = 'browser storage (localStorage, sessionStorage, indexedDB, document.cookie) only in core/prefs (D2/D6)';
const STORAGE = [
  {
    selector: 'Identifier[name=/^(localStorage|sessionStorage|indexedDB)$/]',
    message: STORAGE_MESSAGE,
  },
  {
    // window['localStorage'], globalThis[`indexedDB`]
    selector:
      'MemberExpression[computed=true]:matches([property.value=/^(localStorage|sessionStorage|indexedDB)$/], [property.quasis.0.value.cooked=/^(localStorage|sessionStorage|indexedDB)$/])',
    message: STORAGE_MESSAGE,
  },
  {
    // document.cookie, window.document.cookie, document['cookie']
    selector:
      "MemberExpression:matches([object.name='document'], [object.property.name='document'], [object.property.value='document']):matches([computed=false][property.name='cookie'], [computed=true][property.value='cookie'], [computed=true][property.quasis.0.value.cooked='cookie'])",
    message: STORAGE_MESSAGE,
  },
];
// D2/D13: every component states `changeDetection: ChangeDetectionStrategy.OnPush` explicitly, wherever it lives.
// Angular 22 made OnPush the implicit default, and the installed @angular-eslint rule
// (prefer-on-push-component-change-detection, enabled below) flags only an explicit Eager/Default opt-out.
// This selector is the task's committed fallback (10.1.2-T1 rule table): it keeps an omitted `changeDetection`
// a lint error, so the contract does not depend on the framework default.
const EXPLICIT_ONPUSH = {
  selector:
    "Decorator[expression.callee.name='Component']:not(:has(ObjectExpression > Property[key.name='changeDetection']))",
  message: 'every component sets changeDetection: ChangeDetectionStrategy.OnPush explicitly (D2/D13)',
};
// Components anywhere (decorator-based, not path- or suffix-based) must not subscribe by hand.
const SUBSCRIBE_IN_COMPONENT = {
  selector:
    "ClassDeclaration:has(Decorator > CallExpression[callee.name='Component']) CallExpression[callee.property.name='subscribe']",
  message: 'use toSignal() or takeUntilDestroyed() (D2)',
};
// Presentational layers: no subscription at all, with or without a decorator.
const SUBSCRIBE_IN_UI = {
  selector: "CallExpression[callee.property.name='subscribe']",
  message: 'use toSignal() or takeUntilDestroyed() (D2)',
};
// Composition roots (core/shell/**, features/*/feature/**) are outside these globs, so they may inject stores.
// Covers inject(X), constructor parameter properties (`private store: X`) and plain constructor parameters.
const INJECTED_TYPE = '/^(HttpClient|.*Repository|.*Store)$/';
const INJECT_MESSAGE = 'presentational ui takes inputs/outputs only: no store, repository or HttpClient injection (D2)';
const INJECT_IN_UI = [
  {
    selector: `CallExpression[callee.name='inject'][arguments.0.name=${INJECTED_TYPE}]`,
    message: INJECT_MESSAGE,
  },
  {
    selector: `TSParameterProperty[parameter.typeAnnotation.typeAnnotation.typeName.name=${INJECTED_TYPE}]`,
    message: INJECT_MESSAGE,
  },
  {
    selector: `MethodDefinition[kind='constructor'] > FunctionExpression > Identifier.params[typeAnnotation.typeAnnotation.typeName.name=${INJECTED_TYPE}]`,
    message: INJECT_MESSAGE,
  },
];

const syntax = (...selectors) => ['error', ...selectors];

const syntaxBlocks = [
  {
    files: at('**/*.ts'),
    rules: { 'no-restricted-syntax': syntax(...SECURITY, EXPLICIT_ONPUSH, ...STORAGE, SUBSCRIBE_IN_COMPONENT) },
  },
  {
    files: at('app/core/prefs/**/*.ts'),
    rules: { 'no-restricted-syntax': syntax(...SECURITY, EXPLICIT_ONPUSH, SUBSCRIBE_IN_COMPONENT) },
  },
  {
    files: at('app/shared/ui/**/*.ts', 'app/features/*/ui/**/*.ts'),
    rules: { 'no-restricted-syntax': syntax(...SECURITY, EXPLICIT_ONPUSH, ...STORAGE, SUBSCRIBE_IN_UI, ...INJECT_IN_UI) },
  },
];

// ---------------------------------------------------------------------------------------------------------------
export default defineConfig(
  {
    ignores: [
      'dist/**',
      '.angular/**',
      'node_modules/**',
      // Generated by 10.1.4-T1 and formatted there (D5).
      'src/app/shared/api/**',
      // Negative fixtures: linted explicitly by tools/web/check-lint-fixtures.mjs (--no-ignore).
      'tools/lint-fixtures/**',
    ],
  },
  {
    linterOptions: { reportUnusedDisableDirectives: 'error' },
  },

  // --- application TypeScript: type-aware ---------------------------------------------------------------------
  {
    files: ['src/**/*.ts'],
    extends: [tseslint.configs.recommendedTypeChecked, angular.configs.tsRecommended],
    processor: angular.processInlineTemplates,
    languageOptions: {
      parserOptions: { projectService: true, tsconfigRootDir: import.meta.dirname },
    },
  },
  // --- fixture TypeScript: syntax-only (the fixtures are outside every tsconfig) ------------------------------
  {
    files: ['tools/lint-fixtures/**/*.ts'],
    extends: [tseslint.configs.recommended, angular.configs.tsRecommended],
    processor: angular.processInlineTemplates,
  },
  // --- rules shared by application and fixture TypeScript -----------------------------------------------------
  {
    files: at('**/*.ts'),
    rules: {
      '@typescript-eslint/no-explicit-any': 'error',
      'no-console': ['error', { allow: ['warn', 'error'] }],
      // D2/D13: every component is OnPush regardless of filename or folder.
      '@angular-eslint/prefer-on-push-component-change-detection': 'error',
    },
  },
  ...importBlocks,
  ...syntaxBlocks,

  // --- templates ----------------------------------------------------------------------------------------------
  {
    files: at('**/*.html'),
    extends: [angular.configs.templateRecommended, angular.configs.templateAccessibility],
    rules: {
      // Not part of templateAccessibility in the installed major; D16 forbids positive tabindex.
      '@angular-eslint/template/no-positive-tabindex': 'error',
      '@angular-eslint/template/no-any': 'error',
    },
  },
);
