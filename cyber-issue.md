# Cyber security issues and fix plan

Security audit on `develop` @ `fb7a59e` (2026-10-02), read-only, covering 4 areas: (A) authentication, sessions and MFA; (B) authorization, tenant isolation and request handling; (C) outbound calls, render node and email; (D) secrets, CI, containers and scripts. Every finding cites a file and line; the Medium findings were re-checked against the code.

**Result:** no Critical or High findings. No real secret leaked in the working tree or in the visible history (the clone is shallow, see CY-27).

Severity: 🟠 Medium · 🟡 Low · ℹ️ Info / to verify. Status: `todo` · `doing` · `done` · `accepted` (risk accepted, with a reason).

## 1. Fix plan

| Wave | When | Items |
|---|---|---|
| W1: quick, low risk | Now (Session 2) | CY-05, CY-06, CY-04, CY-09, CY-10, CY-18, CY-19 |
| W2: auth hardening (phase P; registration never opens — DEC 10.14) | With the auth tasks | CY-01, CY-02, CY-03, CY-07, CY-11, CY-12, CY-13, CY-14 |
| W3: hardening / config guards | Before the first production deploy | CY-15, CY-16, CY-17, CY-20, CY-21, CY-22, CY-23 (CY-08 is an accepted phase-P risk, not a fix — DEC 10.14 leaves it accepted permanently) |
| W4: verify | Alongside the related task | CY-24 to CY-30 |

## 2. Medium

| ID | Area | Status | Issue | Location | Risk | Fix |
|---|---|---|---|---|---|---|
| CY-01 | A | todo | No per-account limit on password guesses; only a per-IP limit (10/min per IP or /64). Identity lockout is off by design (BD2); the 09.3.3 per-account counters and the CAPTCHA were dropped with DEC 10.14 (no public sign-up), so the per-IP limit is the only cap. | `src/NightTales.Infrastructure/DependencyInjection/IdentityServiceCollectionExtensions.cs:59`, `src/NightTales.Infrastructure/Identity/IdentityCredentialVerifier.cs:65` | Credential stuffing or password spraying against one e-mail from many IPs takes over `User` accounts without MFA. | Per-account failure counter in PostgreSQL with progressive delay or soft lock, plus an alert; keep the response uniform (no e-mail enumeration). |
| CY-02 | A | todo | Access tokens are not checked against revocation: no `JwtBearerEvents.OnTokenValidated`; the `stamp` claim is checked only by the jobs dashboard. | `src/NightTales.Api/DependencyInjection/AuthServiceCollectionExtensions.cs:34-58` | After a user is suspended, MFA changes, refresh reuse is detected or a recovery-code login, a stolen access token still works for 15 min (admin 5 min). | In `OnTokenValidated`, check the `sid` family is live and the stamp matches (short cache, invalidated by the `pg_notify` security channel), or record the window as accepted. |
| CY-03 | A | todo | Admin MFA enrolment is broken: `RefreshSessionService` issues an enrolment token, but the endpoint's `_ =>` arm drops it. Once wired up, `RequireReauthFilter` skips reauth for enrolment tokens and `EfMfaEnrolmentService` never checks `TwoFactorEnabled`. | `src/NightTales.Api/Endpoints/AuthEndpoints.cs:153-156`, `src/NightTales.Infrastructure/Security/RefreshSessionService.cs:146-152`, `src/NightTales.Api/Http/Filters/RequireReauthFilter.cs:61-66`, `src/NightTales.Infrastructure/Security/EfMfaEnrolmentService.cs:74,116` | Today an Owner or Admin without MFA cannot enrol. After the fix, an older enrolment token (≤ 15 min) could replace the admin's authenticator without reauth. | Return the token in the response body; reject enrolment-scoped callers when `TwoFactorEnabled` is already true; validate the stamp of enrolment tokens (CY-02). |
| CY-04 | C | todo | SMTP can be downgraded to plaintext: outside Development the validator only rejects `Security = None` and accepts `StartTlsWhenAvailable`. | `src/NightTales.Infrastructure/Email/EmailOptionsValidator.cs:99-103`, `SmtpOptions.cs:49-50`, `MailKitSmtpEmailSender.cs:227-233` | STRIPTLS: password-reset links and SMTP credentials are sent in cleartext. | Outside Development with a non-loopback host, allow only `StartTls` or `SslOnConnect`. |
| CY-05 | D | todo | CI does not run on push to `develop`: the push trigger is `[main]`, but the trunk is `develop` (the workflow's own comment says so). | `.github/workflows/backend.yml:4-6` | Merge results on the trunk are never built or tested. | Change the push trigger to `[develop]`; add branch protection that requires the checks. |
| CY-06 | D | todo | gitleaks runs only in local git hooks, not in CI. | `.githooks/pre-commit`, `.githooks/pre-push`, `.github/workflows/backend.yml` | `--no-verify`, a clone without `core.hooksPath`, or a web-UI edit pushes secrets unscanned. | Add a gitleaks job pinned by SHA, and/or turn on GitHub secret scanning and push protection. |
| CY-07 | D | todo | VieNeu Python dependencies are installed without a lock when upstream has no `uv.lock` (plain `uv sync`). | `worker-node/bin/bootstrap.sh:76-80` | Supply-chain risk (floating transitive PyPI packages) on the machine that holds the Keychain secrets. | Commit the generated `uv.lock` (or a hashed requirements file) under `worker-node/` and sync with `--frozen`. |
| CY-08 | D | accepted (P) | All OpenAI traffic goes through a third-party relay (01 DEC-9). | `worker-node/pins.env:21`, `DECISIONS.md` DEC-9 | The relay operator sees every prompt, story and image, and the API key. | Accepted for the personal-only product (DEC 10.14: there is no phase S, so the risk is not scheduled for removal). |

## 3. Low

| ID | Area | Status | Issue | Location | Fix |
|---|---|---|---|---|---|
| CY-09 | B | todo | A cancelled episode still gets a signed video URL: the query does not exclude `Status == Cancelled` (`GetEpisodeByIdHandler` does). | `src/NightTales.Api/Http/EpisodeVideoUrlIssuer.cs:82` | Add `&& candidate.Status != EpisodeStatus.Cancelled`, plus an integration test. |
| CY-10 | B | todo | The `video-url` endpoint has no rate limit, and every call writes an `audit_log` row. It also lacks `RequireCors(SpaCorsPolicy)`. | `src/NightTales.Api/Endpoints/EpisodeVideoEndpoints.cs:41-42` | `.RequireRateLimiting(...)`, or dedupe the audit per (user, episode, TTL); add CORS. |
| CY-11 | A | todo | Recovery codes are stored as unsalted SHA-256 (50-bit entropy). | `src/NightTales.Infrastructure/Security/CrockfordBase32.cs:193` | HMAC-SHA256 with a server pepper (from secrets), or a per-user salt. |
| CY-12 | A | todo | The MFA guess window lives in memory (singleton): it resets on restart, multiplies per replica, and returns 429 at 10k tracked keys (DoS lever). | `src/NightTales.Infrastructure/Security/InMemoryMfaGuessWindow.cs`, `SecurityServiceCollectionExtensions.cs:77` | Keep the counter in PostgreSQL, in the same transaction. |
| CY-13 | A | todo | Logout with an expired access token returns 401; the session family stays live on the server for up to 14 days. | `src/NightTales.Api/Endpoints/AuthEndpoints.cs:299-303` | Allow logout on the refresh cookie plus the Origin check (the cookie is SameSite=Strict). |
| CY-14 | A | todo | `reauth_attempts` is never reset on a successful reauth and is inherited by successors, so 5 failures across 14 days revoke the session. | `src/NightTales.Infrastructure/Security/EfRefreshTokenService.cs:637,800-805` | Reset the counter in `MarkReauthenticatedSql`. |
| CY-15 | B, C, D | todo | Dev storage credentials (`AccessKey: dev`, `SecretKey: dev-secret-local`, `http` ServiceUrl) are in the base `appsettings.json`, with no production guard (unlike the e-mail pepper). | `src/NightTales.Api/appsettings.json:18-19`, `src/NightTales.Worker/appsettings.json:60-66`, `S3ClientFactory.cs:79` | Move them to `appsettings.Development.json`; in `ValidateOnStart`, reject the placeholder and require https for a non-loopback `ServiceUrl`. |
| CY-16 | C | todo | The e-mail link origin is not required to be https (base Worker config: `http://localhost:4200`). | `src/NightTales.Infrastructure/Email/ConfigurationEmailLinkOrigin.cs:39-44` | Outside Development, require `https` and an empty path. |
| CY-17 | C | todo | mock-mpt falls back to the default API key and has no environment guard, while its Dockerfile says it may run as a staging node. | `tools/mock-mpt/MockMptEnvironment.cs:111-114`, `tools/mock-mpt/Dockerfile:54-55` | Refuse the default key unless `ASPNETCORE_ENVIRONMENT=Development`. |
| CY-18 | B | todo | Catalog responses to authenticated requests are `Cache-Control: public`, so a CDN could serve them to anonymous users. | `src/NightTales.Api/Endpoints/CatalogEndpoints.cs:151` | Use `private, max-age=…`, or make the queries `[AllowAnonymousRequest]` deliberately. |
| CY-19 | D | todo | `actions/checkout` does not set `persist-credentials: false`. | `.github/workflows/backend.yml:42,145,213,279,288` | Add `persist-credentials: false`. |
| CY-20 | D | todo | Images not pinned by digest: `postgres:17-alpine` (compose) and `postgres:17` (CI services). Dependabot has no `docker` ecosystem. | `docker-compose.yml`, `.github/workflows/backend.yml:130,200`, `.github/dependabot.yml` | Pin by digest; add a `docker` entry for `src/NightTales.Api`, `src/NightTales.Worker` and `tools/mock-mpt`. |
| CY-21 | D | todo | CODEOWNERS covers only `tests/NightTales.Architecture.Tests/AllowLists.cs`. | `.github/CODEOWNERS` | Add owners for `.github/workflows/`, `.gitleaks*`, `tools/ci/`, `Directory.Packages.props` and the Dockerfiles. |
| CY-22 | D | todo | Design mockups use real-looking personal e-mail addresses (gmail/outlook). | `design/ui/screens/02-public.html`, `design/ui/screens/08-admin.html` | Replace with `@example.com`. |
| CY-23 | C | todo | The breached-password HTTP client does not require https, follows redirects, and reads the body without a size cap. | `src/NightTales.Infrastructure/DependencyInjection/IdentityServiceCollectionExtensions.cs:109,154-158`, `src/NightTales.Infrastructure/Identity/KAnonymityRangeChecker.cs:60` | Require https, `AllowAutoRedirect = false`, `MaxResponseContentBufferSize` ≈ 1 MB. |

## 4. Info / to verify

| ID | Area | Status | Item | Location | Next step |
|---|---|---|---|---|---|
| CY-24 | C | todo | `mpt.bgm_file` accepts free text when `bgm_type = custom`. If MPT opens it as a local path, a node-key holder can read any node file into the audio track. | `src/NightTales.RenderNode.Server/SubmitValidator.cs:273`, `src/NightTales.RenderNode.Contract/MptSubmitFields.cs:41` | Check MPT's behaviour; require `""` or a name inside a fixed music directory. |
| CY-25 | B | todo | Hangfire dashboard: `InternalJobs:AllowedCidrs` defaults to empty (any IP). Auth is still strong (owner/admin + reauth + stamp). | `src/NightTales.Api/Internal/Jobs/JobsDashboardAuthorizationFilter.cs:343`, `src/NightTales.Api/appsettings.json:34` | Refuse to start outside Development when the list is empty. |
| CY-26 | B | todo | `SetFeatureFlagCommand` is `[AllowAnonymousRequest]`; only the Worker CLI sends it today. | `src/NightTales.Application/Features/Platform/Flags/Commands/SetFeatureFlagCommand.cs:28` | Architecture test: no Api endpoint sends an `[AllowAnonymousRequest]` message outside Auth; or `IRequireRole(Owner)` with a system actor. |
| CY-27 | D | todo | The secret scan covered only a shallow clone; commit `fbd413ac` (allow-listed in `.gitleaks.toml`) was not visible. | `.gitleaks.toml` | Run `gitleaks git` on a full clone. |
| CY-28 | A | todo | Password reset / e-mail verify (`IUserActionTokenService`) has no implementation on `develop` yet. | 09.3.3-T1 (`feature/09.3.3-T1-s2`) | Review single-use, hashing at rest and expiry before its PR. |
| CY-29 | A | todo | Minor auth notes: no refresh rotation grace (two tabs → reuse → logout); refresh always issues `amr=pwd`; an enrolment TOTP code does not set `last_totp_step`; a cookie with `remaining <= 0` becomes a session cookie; duplicate log EventId 2400; jobs dashboard skips the stamp check when it is null/empty; login timing for a user with a null `PasswordHash`. | `RefreshSessionService.cs:262`, `Rfc6238TotpVerifier.cs:260-267`, `RefreshCookie.cs:50`, `MfaPreAuthCookie.cs:58`, `JobsDashboardAuthorizationFilter.cs:118` | Triage one by one; fix during the auth hardening wave. |
| CY-30 | B, C, D | todo | Other notes: the EF tenant filter captures the context as a constant (confirm with a test that pooled contexts never mix users); redact `?ticket=` in reverse-proxy logs; IPv6 auth throttle per address (bucket by /64); Hangfire on legacy serializer defaults; render-node cert pin needs a config change on each rotation; Keychain items readable by `/usr/bin/security` without a prompt; commit author e-mail exposes a local hostname. | `TenantFilterConvention`, `AuthFailureThrottle.cs:179-187`, `JobsServiceCollectionExtensions.cs:139-151`, `RenderNodeConnectionFactory.cs:87-136`, `worker-node/bin/secrets.sh:44-45,77` | Document or fix as each area is touched. |

## 5. What is already good (keep it)

- **Tenant isolation:** global filter on every `IUserOwned`; foreign resources answer 404; the only `IgnoreQueryFilters` is `SystemWorkQuery`.
- **Injection:** all SQL is parameterised; no SSRF sink takes user input; `Process` uses `ArgumentList`; e-mail HTML is encoded and CR/LF is refused in subjects.
- **Tokens:** JWT ES256 with issuer, audience, lifetime and 30 s skew; refresh rotation with advisory lock + `FOR UPDATE` and family revoke on reuse; tokens and hub tickets stored hashed.
- **Cookies:** `__Secure-`/`__Host-`, HttpOnly, Secure, SameSite=Strict, narrow path, Origin check on `/refresh` and `/logout`.
- **MFA:** TOTP ±1 step with `FixedTimeEquals` and `last_totp_step` anti-replay; secret protected by DataProtection.
- **Render node:** API key compared via SHA-256 + `FixedTimeEquals`, auth before routing, body caps, MPT URL loopback-only, redirects off, cert pin outside Development.
- **HTTP:** strict CORS allow-list, security headers + CSP, ProblemDetails without stack traces, forwarded headers trusted only from configured proxies.
- **CI and containers:** `permissions: contents: read`, actions pinned by SHA, no `pull_request_target`; images run as non-root and base images are pinned by digest; secrets travel via Keychain/stdin, never argv.
