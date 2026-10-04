# NightTales web

Angular workspace for the NightTales Studio SPA (plan 10). This file holds only what is not obvious from the
scripts; the contract is `plans/features/10-web-ui/README.md` (D1-D18).

## Content Security Policy and Trusted Types (README D4, WR2, WR12, WR13)

The policy is a **response header**, never a `<meta>` tag and never patched from JavaScript.

- `npm run build && npm run csp:gen` writes exactly one file, `dist/nginx/csp-headers.conf`, outside the served
  `dist/web/browser` tree. It holds one `add_header Content-Security-Policy "..." always;` line. Nothing generated is
  committed. The web `Dockerfile` builder stage runs both commands, so the image cannot ship without the policy;
  `web.yml` runs them again for validation only.
- `tools/web/gen-csp.mjs` SHA-256-hashes every executable inline `<script>` of the built HTML into `script-src`
  (JSON / import-map data blocks are not hashed). It **fails the build**, naming file and line, on an inline `on*=`
  handler, a `javascript:` URL, a `<script src>` that is not same-origin, a policy over 8 000 characters, or a
  produced file that breaks the D4 contract. There is no bypass flag.
- A new inline script is hashed by the build automatically. Never whitelist it with `'unsafe-inline'` or a CDN origin;
  prefer an external, self-hosted file. `optimization.styles.inlineCritical` must stay `false` (its injected `onload`
  handler would fail `csp:gen`).
- The production document has no `<base>` (`base-uri 'none'`): `angular.json` sets `deployUrl: "/"` so assets are
  root-relative and `app.config.ts` provides `APP_BASE_HREF`. `ng serve` does not honour `deployUrl`, so the
  `development` build configuration sets `baseHref: "/"` instead (dev sends no CSP, so a `<base>` is harmless there);
  `e2e/deep-link.spec.ts` reloads a nested route on both targets. Because the production document has no `<base>`,
  every runtime fetch (Transloco loader included) must use a root-relative URL (`/assets/...`), never `assets/...`.
- Media origins: the file keeps `${NTS_CSP_MEDIA_ORIGINS}` literal in `img-src 'self' data:${...}` and
  `media-src 'self'${...}` (no space before the placeholder); the image's envsubst fills it at container start from
  `NTS_CSP_MEDIA_ORIGINS` (space-separated `http(s)://host[:port]` origins, empty by default).
  `docker-entrypoint.d/16-nts-media-origins.envsh` gives each validated origin a leading space, so an empty list
  leaves exactly `img-src 'self' data:` / `media-src 'self'`. The container's entrypoint rejects a malformed value.
  To add an origin, set that variable; never widen `img-src` / `media-src` to `*`.
- Trusted Types: the header allow-lists exactly `angular`, `angular#bundler` and `nts-sanitizer`.
  `core/security/nts-sanitizer.ts` is the only module that may create a policy (and the only file exempt from the
  `bypassSecurityTrust*` lint ban); `shared/util/download-blob.ts` + `safe-filename.ts` are the only download path.
- `ng serve` sends no CSP. The runtime proof is the container run: `NTS_E2E_TARGET=container npm run e2e` against the
  image (`e2e/security/*`); every spec imports `test` from `e2e/fixtures/csp.fixture.ts`, which fails on any CSP or
  Trusted Types violation.
