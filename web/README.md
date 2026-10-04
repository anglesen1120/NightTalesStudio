# web

Angular workspace of the NightTales Studio UI. Contracts live in `plans/features/10-web-ui/README.md`.

## Translations (README D10)

- One **scope** per feature: `web/src/assets/i18n/<scope>/{vi,en}.json`. Both files are mandatory; `vi` is the default.
- `core` is Transloco's root translation (keys are used as written, e.g. `common.close`, `core.ui.pageHeader.back`).
  Every other scope is declared on its lazy route with `provideTranslocoScope('<scope>')`; its file keys are prefixed
  with the scope name in code: `quality/vi.json` `{ "hook_missing": … }` is used as `quality.hook_missing`.
- Key style `scope.area.item`, camelCase leaves, no sentence fragments outside a scope. Keys handed over by other plans
  are used verbatim (`series.style.<name>.label/.hint`, `series.style.trueCrimeNotice`, `voices.tag.<tag>`,
  `quality.*`, `render.*`).
- Dates, numbers, relative times and schedule times go through `LocaleService`; no screen calls `Intl` directly.
  `LocaleService.activate(locale, { persist })` is the only way to change the language.
- `npm run i18n:check` (CI) fails on a key present in one language only, a duplicate or non-string leaf, a missing
  language file, a missing handed-over key, and a key used in code that no file defines. `npm run i18n:find` lists
  used and unused keys against the files (dev aid, not a CI step).
