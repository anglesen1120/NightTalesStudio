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
import { defineConfig } from 'eslint/config';
import angular from 'angular-eslint';
import tseslint from 'typescript-eslint';

// Wave-1 features (README D2). Adding a feature = adding it here (cross-feature deep imports are then banned for it).
const FEATURES = ['public', 'auth', 'dashboard', 'series', 'episodes'];

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
    '@shared/ui/**',
    '../data-access/**',
    '../application/**',
    '../ui/**',
  ],
  message: 'domain imports only its own files and @shared/util (D2)',
};
// `@shared/api` is already reported by API wherever both apply, so it is not repeated here (one report per import).
const UI = {
  group: ['@angular/common/http', '@angular/common/http/**', '**/data-access/**', '**/application/**'],
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
// Syntax bans (no-restricted-syntax)
// ---------------------------------------------------------------------------------------------------------------
const SECURITY = [
  {
    selector: "MemberExpression[property.name=/^bypassSecurityTrust/]",
    message: 'bypassSecurityTrust* is banned (D2/D4); sanitise through the Angular sanitizer or core/security',
  },
  {
    selector: 'Identifier[name=/^(eval|Function)$/]',
    message: 'eval / new Function are banned (D2/D4)',
  },
  {
    selector: "CallExpression[callee.object.name='document'][callee.property.name='write']",
    message: 'document.write is banned (D2/D4)',
  },
];
const STORAGE = {
  selector: 'Identifier[name=/^(localStorage|sessionStorage)$/]',
  message: 'browser storage only in core/prefs (D2/D6)',
};
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
const INJECT_IN_UI = {
  selector: "CallExpression[callee.name='inject'][arguments.0.name=/^(HttpClient|.*Repository|.*Store)$/]",
  message: 'presentational ui takes inputs/outputs only: no store, repository or HttpClient injection (D2)',
};

const syntax = (...selectors) => ['error', ...selectors];

const syntaxBlocks = [
  {
    files: at('**/*.ts'),
    rules: { 'no-restricted-syntax': syntax(...SECURITY, EXPLICIT_ONPUSH, STORAGE, SUBSCRIBE_IN_COMPONENT) },
  },
  {
    files: at('app/core/prefs/**/*.ts'),
    rules: { 'no-restricted-syntax': syntax(...SECURITY, EXPLICIT_ONPUSH, SUBSCRIBE_IN_COMPONENT) },
  },
  {
    files: at('app/shared/ui/**/*.ts', 'app/features/*/ui/**/*.ts'),
    rules: { 'no-restricted-syntax': syntax(...SECURITY, EXPLICIT_ONPUSH, STORAGE, SUBSCRIBE_IN_UI, INJECT_IN_UI) },
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
