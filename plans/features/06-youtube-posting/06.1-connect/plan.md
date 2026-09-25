# 06.1 — Connect a YouTube channel (Google project, OAuth connect/callback, channel connection)

> Contract: `../README.md` D1–D5, D11, D17–D20, §8.1; high-level `plans/06-youtube-posting.md` 06.1.1/06.1.2, §5.1; consumers `09` (BD1/BD3/BD9–BD14, WT17/WT27/WT43/WT54), `13` D1–D4, `11` D15.
> Tasks in this folder: **06.1.1-T1 (S)**, **06.1.2-T1 (M)**, **06.1.2-T2 (M)** — all P · M1. Siblings: `06.2.1-publication-consent`, `06.2.2-autopilot-veto`, `06.2.3-upload`, `06.2.4-schedule-status`, `06.3.1-quota-audit`, `06.4-revocation` (same README).
> Legend: ✅ read from source/docs · ⚠️ unverified · [DECISION] README §3 · [OPEN] · [INFERENCE].

## 1. Scope

**In.** Everything that makes one YouTube channel publishable by one owner: two Google Cloud projects (staging Testing, prod In-production unverified) with consent screens and confidential web OAuth clients (06.1.1-T1); `POST /channels/{platform}/connect` (re-auth, legal gate, `state` + PKCE, 10 min single use) and `GET /channels/youtube/callback` (state claim, code exchange with the client bound to the state, `channels.list?mine=true`, one-owner-per-channel rule, audit, connect e-mail, fixed redirects) (06.1.2-T1); the `ChannelConnection` aggregate with `ITokenProtector`-encrypted tokens, the pinned `oauth_client_id`, `IOAuthClientResolver`, `ChannelConnectionDto`, `GET /channels` and the `channel_connected` e-mail template (06.1.2-T2).

**Out (other folders, same plan).** `DELETE /channels/{id}` + revoke + deletion request (06.4.1-T1), `TokenHealthJob` and the 25-day data refresh (06.4.2-T1/T2), publications/consent/veto (06.2.x), quota ledger (06.3.1-T1), the upload client half of `IYouTubeClient` (06.2.3-T1), `platform_incidents` (06.2.3-T3) and `OAuthErrorClassifier` (06.2.3-T3/T1).

**Out (S phase, Gate 2).** Multi-client settings from 09.10, the client-removal guard and per-client connection counts (06.1.2-T3, F19); TikTok (07); every legal text (13.1.x) and the acceptance endpoints/UI (13.2.1-T1).

**Explicit non-goal.** No outbox/realtime event is raised by the two connect calls **except** `channel_connection_changed.v1` on a successful callback (§5.3): the browser already navigates to a freshly fetched `/channels`, so a realtime frame would be a second mechanism for the same fact.

**Reuse rule.** No second e-mail path (`IEmailSender`, 09.2.6-T3), no second flag path (`RequireFeature`, 09.9.2-T2), no second audit mechanism (`[Audited]`/`IAuditData`, 09.3.6-T1), no second token mechanism (JWT/`refresh_tokens`, 09.3.2-T1), no second crypto path (`ITokenProtector`, 09.9.4-T1), no second hashing helper (`ITokenIssuer.HashToken`).

## 2. Confirmed facts

| Id | Fact | Evidence |
|---|---|---|
| A1 | Routes are platform-parameterised: `GET /channels`, `POST /channels/{platform}/connect` (returns the OAuth URL), `GET /channels/{platform}/callback` | ✅ `plans/00-overview.md:361` |
| A2 | `channel_connections` columns: `access_token_enc`, `refresh_token_enc`, `token_expires_at`, `status` (`active`/`revoked`/`expired`/`error`), `oauth_client_id`, `last_verified_at`, `revoked_at` (+ `user_id`, `platform`, `external_account_id`, `display_name`) | ✅ 06 README C11 |
| A3 | Secrets: `nts/<env>/api/YOUTUBE_CLIENT_SECRET` (Api role); P reads settings from the environment, 09.10 is S | ✅ 06 README C12; 11 README P4:70 |
| A4 | `ITokenProtector.Protect(string purposeSuffix, string plaintext)`; effective purpose = `tokens.v1/{purposeSuffix}`; `TryUnprotect` false on a wrong purpose; the TOTP store uses `CreateProtector("identity.totp.v1")` directly and must not be routed through this port | ✅ `09.9-platform/09.9.4-T1-security-hardening.md:64-79, :79, :207` |
| A5 | `IReauthGuard.RequireRecentAsync(TimeSpan window, ct) → ErrorOr<Success>`; 401 `reauth_required`; window `Auth:Reauth:WindowMinutes` = 5; `RequireReauth(TimeSpan)` is a thin endpoint filter over it | ✅ `09.3-auth/09.10.4-T1-totp-and-reauth.md:52, :97, :116-118, :150` |
| A6 | `RequireFeature("youtube_publish")` → **404** `feature_disabled`, handler not invoked, no 403; consumers attach the guard themselves | ✅ `09.9-platform/09.9.2-T2-feature-flags-read-service.md:9, :73-83, :130` |
| A7 | `ILegalAcceptance.EnsureCurrentAsync(userId, ct) → ErrorOr<Success>`; failure = **409** `legal_acceptance_required` with `errors.documents = ["tos","privacy"]`; called by 06.1.2-T1 | ✅ `13-compliance/README.md:46, :80` |
| A8 | `IEmailSender.QueueAsync(EmailMessage) → ErrorOr<Guid>`; `EmailMessage(Guid UserId, string Template, Language Locale, IReadOnlyDictionary<string,string> Params)`; catalog is code (`EmailTemplateCatalog`) and `channel_connected.*` is added by 06; four files `vi`/`en` × `txt`/`html`; `ValidateOrThrow` at boot fails on an unknown `{{key}}` or a missing file; `params` is nulled on `sent`; a token-bearing template links via `IEmailTokenMinter` at send time (WT17) | ✅ `09.2.6-worker-hangfire/09.2.6-T3-email-pipeline.md:21-39, :77-97, :125` |
| A9 | `ITokenIssuer.HashToken(string token) → SHA-256 hex, "the only stored form"`; `refresh_tokens.family_id` + `revoked_at` + `expires_at`; `ICurrentUser.SessionId` is the family id | ✅ `09.3-auth/09.3.2-T1-login-refresh-logout.md:36, :86-105, :119-140`; 09 README BD3 (`sid`) |
| A10 | `ETags` helper + `RequireIfMatch()` (missing header → 428 `if_match_required`, mismatch → 412 with the resource code) live in 09.1.4-T1 | ✅ `09.1.4-error-mapping/09.1.4-T1-problem-details-etag-helpers.md:53-55, :69, :117` |
| A11 | G1–G13 as reviewed for this plan: scopes `youtube.upload`/`youtube.readonly` (G12), Testing project = 7-day refresh tokens + ≤100 test users (G10), unverified projects are upload-restricted to `private` (G2), revoke = `POST https://oauth2.googleapis.com/revoke?token=…` (G9), PKCE documented for the installed-app flow only (G11) | ✅ 06 README §1 G-rows |
| A12 | P settings are environment values; the secret manager is the only source of `YOUTUBE_CLIENT_SECRET`; env file / key material never in the repo | ✅ 11 README §1 P4, D15 (`11-worker-deployment/README.md:70, :226`) |

## 3. Domain and data

### 3.1 Aggregates

**`OAuthState`** (new, owner 06.1.2-T1) — `src/NightTales.Domain/Publishing/OAuthState.cs`.
```csharp
public sealed class OAuthState : AggregateRoot           // no IUserOwned: the callback is anonymous
{
    public Guid Id { get; private init; }
    public Guid UserId { get; private init; }
    public Platform Platform { get; private init; }
    public string StateHash { get; private init; }        // SHA-256 hex of the 32-byte base64url state
    public string CodeVerifierProtected { get; private init; }  // ITokenProtector purpose tokens.v1/oauth.v1
    public string OAuthClientId { get; private init; }
    public Guid RefreshFamilyId { get; private init; }    // bound to the live session that started the connect
    public string BrowserNonceHash { get; private init; }  // YR10: SHA-256 hex of the __Host-nts_oauth cookie value
    public DateTimeOffset CreatedAt { get; private init; }
    public DateTimeOffset ExpiresAt { get; private init; }
    public DateTimeOffset? UsedAt { get; private set; }

    public static OAuthState Create(Guid userId, Platform platform, string stateHash, string codeVerifierProtected,
                                    string browserNonceHash, string oauthClientId, Guid refreshFamilyId,
                                    DateTimeOffset now, TimeSpan ttl);
    public bool IsUsable(DateTimeOffset now) => UsedAt is null && ExpiresAt > now;
    public void Claim(DateTimeOffset now);                // UsedAt = now; throws if !IsUsable
}
```
The state row is **not** `IUserOwned`: the callback has no principal, so the authentication is the state **plus** the `__Host-nts_oauth` browser nonce (README D3/F01; YR10) — neither binding alone completes a connect. Every read of it therefore goes through the explicit `state_hash` + `user_id` predicate, never a global filter — and `UpdatedAt`/`xmin` bookkeeping stays off this table (no ETag).

**`ChannelConnection`** (new, owner 06.1.2-T2) — `src/NightTales.Domain/Publishing/ChannelConnection.cs`, `IUserOwned`.
```csharp
public enum Platform { YouTube }                          // 07 TikTok is S; stored as text + HasEnumCheck
public enum ChannelConnectionStatus { Active, Revoked, Expired, Error }

public sealed class ChannelConnection : AggregateRoot, IUserOwned
{
    public Guid UserId { get; private init; }
    public Platform Platform { get; private init; }
    public string? ExternalAccountId { get; private set; }   // Google channel id (opaque, never a name); NULL after PurgeYouTubeData
    public string? DisplayName { get; private set; }         // channel title; refreshed by 06.4.2-T1; NULL after PurgeYouTubeData
    public ChannelConnectionStatus Status { get; private set; }
    public string AccessTokenEnc { get; private set; }
    public string RefreshTokenEnc { get; private set; }
    public DateTimeOffset TokenExpiresAt { get; private set; }
    public string Scope { get; private set; }
    public string OAuthClientId { get; private set; }        // pinned at Connect, re-pinned only by Reconnect (YR11)
    public DateTimeOffset? LastVerifiedAt { get; private set; }
    public DateTimeOffset ConnectedAt { get; private init; }
    public DateTimeOffset? RevokedAt { get; private set; }

    public static ChannelConnection Connect(Guid id, Guid userId, Platform platform, string externalAccountId,
        string displayName, string accessTokenEnc, string refreshTokenEnc, DateTimeOffset tokenExpiresAt,
        string scope, string oauthClientId, DateTimeOffset now);
    public void Reconnect(string displayName, string accessTokenEnc, string refreshTokenEnc,
        DateTimeOffset tokenExpiresAt, string scope, string oauthClientId, DateTimeOffset now);
    // YR11: same row (loaded FOR UPDATE), status → active, oauth_client_id re-pinned to the state's client
    public void MarkExpired(DateTimeOffset now);      // 06.2.3-T2 / 06.4.2-T1
    public void MarkError(DateTimeOffset now);        // 06.4.2-T1
    public void MarkRefreshed(string accessTokenEnc, string refreshTokenEnc,
        DateTimeOffset tokenExpiresAt, DateTimeOffset now);   // 06.4.2-T1; refuses a revoked row
    public void RefreshFromApi(string displayName, DateTimeOffset now);   // 06.4.2-T1; DisplayName + LastVerifiedAt only
    public void Revoke(DateTimeOffset now);           // 06.4.1-T1; nulls both ciphertexts
    public void PurgeYouTubeData(DateTimeOffset now);  // 06.4.3-T1; nulls tokens + DisplayName + ExternalAccountId; requires status == Revoked
}
```
Setter contract (checked by the domain tests, not by prose): every property a declared method writes is `private set` — `ExternalAccountId`, `DisplayName`, `OAuthClientId`, `Status`, `AccessTokenEnc`, `RefreshTokenEnc`, `TokenExpiresAt`, `Scope`, `LastVerifiedAt`, `RevokedAt`; the properties no method writes after construction stay `private init` (`UserId`, `Platform`, `ConnectedAt`, and every field of `OAuthState` except `UsedAt`). `Reconnect` writing `OAuthClientId` is exactly why that one is `private set`, not `private init` (YR11).

Invariants: `Connect` generates the id **before** encryption (D4: the protection purpose is `tokens.v1/{connectionId}`); `Status == Active` is the only state an upload may use; `Revoke` is the only path that nulls the token columns (owned by 06.4.1-T1, declared here); `Reconnect` resets `RevokedAt` to null and sets `LastVerifiedAt = now`; `MarkRefreshed` refuses a `revoked` row (`InvalidOperationException` — a revoked connection becomes `active` only through `Reconnect`) and is the only path outside `Connect`/`Reconnect` that writes the token columns; `RefreshFromApi` writes only `DisplayName`/`LastVerifiedAt` and never touches `Status` or the tokens (one writer per column, no second setter for `LastVerifiedAt`); `PurgeYouTubeData` requires `Status == Revoked` (`InvalidOperationException` otherwise — the deletion engine only ever purges an already-disconnected channel, per the 06.4.1-T1 → 06.4.3-T1 dependency order) and is the only path that writes `ExternalAccountId`/`DisplayName` to `null`.

**CR accepted (06.4-revocation §10.1 item 6 / 06.4.3-T1's "nullable-column alternative").** `ExternalAccountId`/`DisplayName` are `NULL`-able (§3.2) specifically so `PurgeYouTubeData` can null them outright instead of writing the `"deleted"`/`"deleted:" + Id` placeholders 06.4.3-T1's Design currently proposes as its default; the placeholder path is **not needed** once the column accepts `NULL`, and 06.4.3-T1's Steps 1 ("tokens null, placeholder metadata, DisplayName/ExternalAccountId non-null") is superseded by this signature — its own file already named this as the preferred outcome.

**CR accepted (06.4-revocation §10.1 item 2).** `MarkRefreshed`/`RefreshFromApi` are declared here, in this folder's owned `ChannelConnection.cs`, with the exact signatures 06.4.2-T1 requested; 06.4.2-T1's Steps still say "Add `MarkRefreshed`…" because that task's commit is what lands the method body and its domain test — the declaration lives in the file 06.1.2-T2 owns, the same convention already used for `Revoke` (06.4.1-T1) and `MarkExpired` (06.2.3-T2/06.4.2-T1) above. No fallback to `Reconnect` is needed.

**`ITokenService` — the one shared refresh path (D9; accepted here, owner 06.1.2-T2).** Both `06.2.3-T2` and `06.4.2-T1` need the same "refresh under lock" read against `ChannelConnection`, and README D9 already names the member without pinning a declaring task; since the lock and the re-read are over this aggregate's own table, this folder is the natural single owner (one seam file, no duplicate implementation):
```csharp
// Application/Abstractions/Publishing/ITokenService.cs — declared by 06.1.2-T2
public sealed record ChannelAccessToken(string AccessToken, DateTimeOffset ExpiresAt);
public interface ITokenService
{
    Task<ErrorOr<ChannelAccessToken>> GetAccessTokenAsync(Guid connectionId, CancellationToken ct);
    // SELECT … FOR UPDATE on the connection row, re-read after the lock (F21); refreshes via
    // IYouTubeClient.RefreshAsync when < 5 min remain, calls MarkRefreshed on rotation,
    // maps invalid_grant -> the caller's own failure handling (never swallowed).
}
```
`TokenService` (Infrastructure, this folder) is Scoped, consumes `IOAuthClientResolver` + `IYouTubeClient` (06.1.2-T1) and `ITokenProtector` (A4); 06.2.3-T2 and 06.4.2-T1 consume the port only — no second implementation, no direct `SELECT … FOR UPDATE` outside this class (closes 06.4-revocation §10.1 item 1).

### 3.2 Tables (expand-only migrations, D20)

`_06_OAuthStates` — owner **06.1.2-T1**; `_06_ChannelConnections` — owner **06.1.2-T2**. Migration name shape `<yyyyMMddHHmm>_06_OAuthStates` (09.9.4-T1:18/D9 naming `<Owner>_<What>`).

```sql
-- 06.1.2-T1
CREATE TABLE oauth_states (
  id                   uuid PRIMARY KEY,
  user_id              uuid NOT NULL,
  platform             text NOT NULL,
  state_hash           text NOT NULL,
  code_verifier_protected    text NOT NULL,
  browser_nonce_hash   text NOT NULL,           -- YR10: SHA-256 hex of the __Host-nts_oauth cookie value
  oauth_client_id      text NOT NULL,
  refresh_family_id    uuid NOT NULL,
  created_at           timestamptz NOT NULL,
  expires_at           timestamptz NOT NULL,
  used_at              timestamptz NULL
);
CREATE UNIQUE INDEX ux_oauth_states_state_hash ON oauth_states (state_hash);
CREATE INDEX ix_oauth_states_user_created ON oauth_states (user_id, created_at DESC);
ALTER TABLE oauth_states ADD CONSTRAINT ck_oauth_states_platform CHECK (platform IN ('youtube','tiktok')); -- (07 CR-13)

-- 06.1.2-T2
CREATE TABLE channel_connections (
  id                  uuid PRIMARY KEY,
  user_id             uuid NOT NULL,
  platform            text NOT NULL,
  external_account_id text NULL,        -- NULL only after 13.3.1-T1's deletion purge (always on an already-revoked row)
  display_name        text NULL,        -- NULL only after the same purge
  status              text NOT NULL,
  access_token_enc    text NULL,          -- NULL only while status = 'revoked' (Revoke nulls them)
  refresh_token_enc   text NULL,
  token_expires_at    timestamptz NOT NULL,
  scope               text NOT NULL,
  oauth_client_id     text NOT NULL,
  last_verified_at    timestamptz NULL,
  connected_at        timestamptz NOT NULL,
  revoked_at          timestamptz NULL
);
CREATE UNIQUE INDEX ux_channel_connections_account
  ON channel_connections (platform, external_account_id) WHERE status <> 'revoked';
CREATE INDEX ix_channel_connections_user_id ON channel_connections (user_id);
ALTER TABLE channel_connections ADD CONSTRAINT ck_channel_connections_platform CHECK (platform IN ('youtube','tiktok')); -- (07 CR-13)
ALTER TABLE channel_connections ADD CONSTRAINT ck_channel_connections_status
  CHECK (status IN ('active','revoked','expired','error'));
```
No FK to `users` (rows outlive the account, 13 §5.4, mirrored from `email_messages` 09.2.6-T3:75). `HasEnumCheck` for `platform`/`status` (09.2.1-T1). Indexes are exactly README D18's two. `Revoke` nulls both token columns and sets `token_expires_at = now`; no `xmin`/ETag column (D15 needs no `If-Match` on this resource). `external_account_id`/`display_name` are nullable so 06.4.3-T1's `PurgeYouTubeData` (13 D4 channel-scope handler) can null them outright instead of writing a `'deleted'`/`'deleted:<id>'` placeholder (closes `06.4-revocation/plan.md` §10.1 item 6); the purge runs only on a row already `status = 'revoked'` (06.4.1-T1 precedes 06.4.3-T1 in the dependency chain), which `ux_channel_connections_account`'s `WHERE status <> 'revoked'` predicate already excludes — so a NULL `external_account_id` can never collide with a live connection, and no index or invariant changes.

### 3.3 Invariants (enforced in the domain, tested)

1. One live connection per `(platform, external_account_id)` — the partial unique index; a second user's insert fails with SQLSTATE `23505` on `ux_channel_connections_account` → 409 `channel_already_connected`. `revoked` rows are excluded → a channel can be reconnected by someone else after a revoke.
2. The same user reconnecting an `active`/`expired`/`error` row updates **that** row (`Reconnect`), never inserts a second one.
3. A connect always pins the client that minted the code (`oauth_states.oauth_client_id`, README D5); **YR11**: `Reconnect` is the only other writer of that column and re-pins it to the state's client, so a reconnect through a different active client moves the connection to that client (nothing else ever changes `oauth_client_id`).
4. An `OAuthState` is usable once (single use) within 10 min, only from the browser that started the connect (`__Host-nts_oauth` cookie, YR10) and only while its `refresh_family_id` is not revoked: the claim is an atomic `UPDATE … RETURNING` **committed in its own unit of work** before any Google call, and `IRefreshTokenService.GetReauthStateAsync(familyId).Revoked` (an existing member, `09.3.2-T1-login-refresh-logout.md:65,67` — no new port) is the liveness read (README D3; residual: a family that expired without a revoke still reads not-revoked, bounded by the 10-minute TTL and the ≤ 5-minute re-auth at connect).
5. Tokens and PKCE verifiers are stored only as `ITokenProtector` ciphertext; the state is stored only as a SHA-256 hash; no plaintext token, verifier or `code` exists after the call returns.

## 4. Use cases

### UC1 `ConnectYouTubeCommand` — `POST /channels/youtube/connect`
`Application/Features/Publishing/Channels/{ConnectYouTubeCommand,ConnectYouTubeHandler,ConnectYouTubeValidator}.cs`

| Step | Behaviour | Error |
|---|---|---|
| 1 | Endpoint filter `RequireFeature("youtube_publish")` | 404 `feature_disabled` (A6) |
| 2 | Bearer required (fallback policy); `platform` route value must be `youtube` | 401 `unauthorized`; 404 `not_found` |
| 3 | `RequireRateLimiting("auth")` | 429 `rate_limited` + `Retry-After` |
| 4 | `IReauthGuard.RequireRecentAsync(TimeSpan.FromMinutes(5), ct)` (A5) | 401 `reauth_required` |
| 5 | `ILegalAcceptance.EnsureCurrentAsync(userId, ct)` (A7) | 409 `legal_acceptance_required` + `errors.documents` |
| 6 | `IOAuthClientResolver.ActiveClientId` → `ResolveAsync(id)`; secret from the secret ref (A3/A12) | 503 `service_unavailable` [INFERENCE code] if the secret is unreadable |
| 7 | Purge this user's finished/expired state rows, then insert one `OAuthState` (id `Guid.CreateVersion7()`, state = 32 random bytes base64url, verifier = 32 random bytes base64url, browserNonce = 32 random bytes base64url, `state_hash = ITokenIssuer.HashToken(state)`, `CodeVerifierProtected = ITokenProtector.Protect("oauth.v1", verifier)`, `BrowserNonceHash = ITokenIssuer.HashToken(browserNonce)` (YR10), `RefreshFamilyId = ICurrentUser.SessionId`, TTL 10 min) | — |
| 8 | Set the cookie `__Host-nts_oauth = browserNonce` (HttpOnly, Secure, SameSite=Lax, Path=/, Max-Age 600) and return `{ authorizationUrl, expiresAt }` (authorization URL from `YouTubeOptions.AuthorizationEndpoint` + client id + `redirect_uri` + scopes + `access_type=offline` + `prompt=consent` + `include_granted_scopes=false` + `state` + `code_challenge`/`code_challenge_method=S256`); the nonce itself is never in the body, the URL or a log line (YR10) | — |

Request: no body. Response `200 ConnectYouTubeResponse(string AuthorizationUrl, DateTimeOffset ExpiresAt)`. Idempotency: deliberately **not** idempotent (each call mints a fresh state); the `auth` rate policy is the bound. Audit: none at this step (README D17 has no state action); the state row is the record. Side effects: one `oauth_states` insert; the per-user purge deletes only that user's own finished rows (bounded, no job needed).

### UC2 `CompleteYouTubeConnectCommand` — `GET /channels/youtube/callback?code&state`
`Application/Features/Publishing/Channels/{CompleteYouTubeConnectCommand,CompleteYouTubeConnectHandler}.cs`. Anonymous endpoint, rate policy `auth`, `[NotAudited]` (the handler writes its own audit row on `Connected` only, §"Audit" below).

| Step | Behaviour | Outcome |
|---|---|---|
| 1 | `state`/`code` present, `state` ≤ 128 chars, and the request carries the `__Host-nts_oauth` cookie | `StateInvalid` |
| 2 | **Unit of work #1 (commits alone).** Claim atomically: `UPDATE oauth_states SET used_at = now() WHERE state_hash = @h AND used_at IS NULL AND expires_at > now() RETURNING id, user_id, code_verifier_protected, oauth_client_id, refresh_family_id` — no row → invalid. Committing this before the Google calls is what makes the state single-use even when the rest fails (`ErrorOr` errors roll a transaction back on 09 D5, hence the success-typed outcome below) | `StateInvalid` |
| 3 | **Unit of work #2 (all-or-nothing).** The `__Host-nts_oauth` cookie's `ITokenIssuer.HashToken` value must equal the row's `browser_nonce_hash`, compared with `CryptographicOperations.FixedTimeEquals` (09 BD16) → mismatch means another browser, not another state (YR10); then `refresh_family_id` not revoked via `IRefreshTokenService.GetReauthStateAsync(...)` (existing port member, A9); Google `error=access_denied` in the query → denied before any exchange. The cookie is cleared on **every** callback outcome (single use) | `StateInvalid` / `Denied` |
| 4 | `TryUnprotect("oauth.v1", code_verifier_protected)` | `StateInvalid` |
| 5 | `IOAuthClientResolver.ResolveAsync(state.oauth_client_id)` — the client bound to the state, never the active one | `ExchangeFailed` |
| 6 | `IYouTubeClient.ExchangeCodeAsync(client, code, verifier, redirectUri, ct)` (no retry: the code is single-use) | `ExchangeFailed` |
| 7 | `IYouTubeClient.GetMyChannelAsync(accessToken)` → `(id, title)`; no channel → failed | `ExchangeFailed` |
| 8 | 06.1.2-T2's `IConnectChannelConnection.ConnectAsync(...)` joins unit #2: it locks the row `FOR UPDATE`, then inserts, or `Reconnect`s under the **existing** connection id with `oauth_client_id` **re-pinned** to the state's client (YR11); tokens are protected for `tokens.v1/{connectionId}` (D4) | `ChannelAlreadyConnected` (23505 on the partial unique) |
| 9 | On `Connected` only: `IAuditWriter` row `publishing.channel_connected` (`data = { platform, oauthClientId, externalAccountId, reconnected }`, `subject = actor = state.user_id`, request `ip`/`user_agent`), outbox `channel_connection_changed.v1 { connectionId, userId, status }`, e-mail `channel_connected` (UC2b) | — |
| 10 | `302 Location: /channels?connected=youtube` (`Referrer-Policy: no-referrer`, `Cache-Control: no-store`) | `302 Location: /channels?error=<wire>` for the four failure outcomes |

The handler returns **`ConnectYouTubeOutcome { Connected, StateInvalid, Denied, ExchangeFailed, ChannelAlreadyConnected }`** and never an error (the 09.3.2-T1 `ReauthOutcome.Failed`/WT29 pattern): the endpoint maps the outcome to the redirect. Unit #2's failure therefore rolls back the connection row, the audit row, the outbox row and the e-mail only, while the claim stays spent — both properties hold at once. A crash between the units burns the state and creates nothing (the user restarts the connect).

Every failure is a redirect to a **fixed** relative path with a fixed `error` query value — never a caller-supplied URL, never the `code`, never the raw `state` (README D3). Step 2 is the CSRF defence: the state is single-use, hashed, 10 min, bound to the user **and** the session family (A9), so a redirect the user did not start cannot succeed.

**UC2b connect e-mail.** `IEmailSender.QueueAsync(new EmailMessage(userId, "channel_connected", user.UiLocale, params), ct)` inside unit of work #2, on the `Connected` path only (A8), `params`: `displayName`, `channelTitle`, `occurredAt`, `device`, `securityLink` (`/account/security`). The template is **not** token-bearing → no `IEmailTokenMinter` and no `link` key (WT17/WT30). `channelTitle` is the user's own channel title; it never reaches a log line (README D17).

### UC3 `ListChannelsQuery` — `GET /channels`
`Application/Features/Publishing/Channels/{ListChannelsQuery,ListChannelsHandler}.cs`. Returns `{ items: ChannelConnectionDto[] }` ordered by `connected_at DESC`; one indexed query on `ix_channel_connections_user_id` + the tenant filter, **no** token columns and **no** per-row follow-up (no N+1, no N+1 count of publications). `ChannelConnectionDto(Guid Id, string Platform, string? ExternalAccountId, string? DisplayName, string Status, DateTimeOffset? LastVerifiedAt, DateTimeOffset ConnectedAt)` (README D4). `status` is the lower-case wire value of the enum; a `null` `ExternalAccountId`/`DisplayName` means the row was purged by 06.4.3-T1's deletion handler after revocation (10 wave 2's channels page renders that as "removed").

**Error codes.** `feature_disabled` 404, `unauthorized` 401, `reauth_required` 401, `legal_acceptance_required` 409 (A7/13 CR-3), `channel_already_connected` 409, `not_found` 404, `rate_limited` 429 (09 §5.4); `oauth_state_invalid` / `oauth_denied` / `oauth_exchange_failed` are the **redirect `error` query values** of the callback; only `oauth_state_invalid` and `channel_already_connected` are also catalogue codes (06 README C6). Per **YR5**, `oauth_denied` and `oauth_exchange_failed` exist **only** as redirect query values — they are registered in **06 CR-9**, not as `plans/09-backend-api.md` §5.4 codes, so this folder does not ask for catalogue rows for them; `service_unavailable` per 09 §5.4.

**Authorization.** Every route carries `RequireFeature("youtube_publish")`; `GET /channels` and `POST /channels/youtube/connect` require a bearer token; the callback is anonymous by design and authenticates through the state. Tenant: `ChannelConnection : IUserOwned` → the 09.2.1-T1 global filter answers 404 for another user's row — no hand-rolled predicate, no existence leak (README D17).

**Audit.** `publishing.channel_connected` only (D17) — written by the callback handler through `IAuditWriter` inside unit of work #2, on the `Connected` outcome only (the 09.7.2-T1 precedent for an anonymous GET that writes its own audit row, `09.7-video-review-storage/plan.md:235`); the row carries `platform`, `oauthClientId`, `externalAccountId`, `reconnected` and never a title, a token, a code or a state. Because a pipeline audit would also fire on the failure outcomes, **both commands carry `[NotAudited]`** — exactly one 09.3.6-T1 marker each, so the arch test passes by design.

**Idempotency.** Neither route is idempotent by header: `POST …/connect` deliberately mints a fresh state per call (bounded by rate policy `auth`), and the callback's single-use state claim is its own idempotency guard — a replay answers `?error=oauth_state_invalid` and exchanges nothing. No `Idempotency-Key` and no ETag is involved on either route (this folder has no mutable resource a client may conditionally write).

## 5. API and events

### 5.1 Routes (all under `/api/v1`, JSON conventions 09 D14)

| Method | Path | Auth | Flag | Rate | Success | Notes |
|---|---|---|---|---|---|---|
| POST | `/channels/youtube/connect` | bearer | `youtube_publish` | `auth` | 200 `{ authorizationUrl, expiresAt }` | re-auth ≤ 5 min, legal gate (UC1) |
| GET | `/channels/youtube/callback?code&state` | anonymous (state) | `youtube_publish` | `auth` | 302 `/channels?connected=youtube` | `Referrer-Policy: no-referrer`, `Cache-Control: no-store` |
| GET | `/channels` | bearer | `youtube_publish` | default | 200 `{ items: [...] }` | tenant-filtered |

`platform` other than `youtube` → 404 `not_found` (07 owns TikTok, S). 302 targets are exactly two fixed relative paths; an unexpected provider payload never reaches the `Location` header.

**Cookies.** `POST …/connect` sets `__Host-nts_oauth` (HttpOnly, Secure, SameSite=Lax, Path=/, 10 min) and never returns the nonce in the body; the callback reads it (constant-time compare) and clears it on every outcome (YR10). No other cookie is used by this folder — the refresh cookie belongs to 09.3.2-T1's `/api/v1/auth` scope and is deliberately not involved.

### 5.2 Status codes

`200`, `302`, `401 unauthorized|reauth_required`, `404 feature_disabled|not_found`, `409 legal_acceptance_required|channel_already_connected`, `429 rate_limited`, `500 internal_error`, `503 service_unavailable`. The callback answers 302 for **every** flow failure (including 401/409 semantics) because a top-level browser navigation cannot render ProblemDetails usefully; the JSON codes above exist in the catalogue for the other routes. 500/503 on `POST …/connect` (state write failure, secret unreadable before any state is written) are JSON, never redirects — that route still returns `ErrorOr`. On the callback, an unreadable secret is step 5 of unit #2 and maps to the success-typed `ExchangeFailed` outcome → 302 `?error=oauth_exchange_failed`, not a 500/503; only a genuinely unexpected exception (e.g. a DB failure inside unit #1's claim) reaches the callback as 500 JSON, before any redirect is attempted.

### 5.3 Events

| Kind | Type | Payload | Raised by | Consumer |
|---|---|---|---|---|
| Outbox | `channel_connection_changed.v1` | `{ connectionId, userId, status }` (ids only, D13) | callback on success (status `active`); 06.4.1-T1 revoke; 06.4.2-T1 expiry | 02 orchestrator (a blocked series resumes when the channel is active again), 10 UI |
| Audit | `publishing.channel_connected` | `{ platform, oauthClientId, externalAccountId, reconnected }` | callback | 13.4.2-T1 evidence pack, 06.5.1-T1 |

No SignalR frame (`RealtimeData` is reserved for `RenderProgress`, WT54); the redirect + refetch covers the browser. No `platform_incident_raised/cleared` here (06.2.3-T3).

## 6. UI

| Surface | Owner | What this folder hands over |
|---|---|---|
| Channels page `/channels` | **10 wave 2, id not yet pinned — [OPEN]** | `GET /channels` list shape, the two fixed redirect outcomes (`?connected=youtube`, `?error=<code>`), the error→copy map |
| Owner re-auth dialog (password + TOTP) | `10.3.3-T3` (10 P slice, moved per plan-10 D-list) | 401 `reauth_required` on connect → open the dialog, then retry the connect |
| Legal acceptance dialog | `13.2.1-T1` + legal pages `10.3.1-T1` | 409 `legal_acceptance_required` + `errors.documents = ["tos","privacy"]` → the dialog, then retry |
| Connect e-mail body | 06.1.2-T2 (template files) | `channel_connected` vi/en, `{{displayName}} {{channelTitle}} {{occurredAt}} {{device}} {{securityLink}}` |

Copy keys the channels page needs (proposed, [INFERENCE] — 10 owns the final names): `channels.connect`, `channels.connected` ("YouTube channel {title} connected"), `channels.error.oauth_state_invalid` ("This link expired or was already used — start again"), `channels.error.oauth_denied` ("You declined access at Google"), `channels.error.oauth_exchange_failed` ("Google did not complete the connection — try again"), `channels.error.channel_already_connected` ("This channel is already connected to another Night Tales account"), `channels.error.feature_disabled` (rendered as the generic not-found page, 10 D), `channels.reauth_required`, `channels.legal_acceptance_required`. No token ever appears in a URL fragment on this page (unlike the veto cancel link, 06.2.2-T2) — the callback query carries `code`/`state` only, and the page reads the two fixed keys `connected`/`error`, which 10 must strip from the address bar after render.

## 7. Jobs and integrations

**No recurring job is owned by 06.1** (`AutopilotConsentJob` 06.2.2-T3, `PublishYouTubeJob` 06.2.3-T2, `PollYouTubeProcessingJob`/`PublicationGoLiveJob` 06.2.5-T1, `TokenHealthJob` 06.4.2-T1). This folder integrates:

| Call | Where | Timeout | Retry | Failure map |
|---|---|---|---|---|
| `POST https://oauth2.googleapis.com/token` (exchange) | callback, Api request thread | `YouTube:Http:TimeoutSeconds` = 15 [INFERENCE key] | **none** — an authorization code is single-use; a retry would burn it | transport/5xx → 302 `?error=oauth_exchange_failed`, the state stays used (no second attempt), the user restarts the connect flow |
| `POST https://oauth2.googleapis.com/token` (refresh) | 06.2.3-T2 / 06.4.2-T1 (not here) | same | 2 attempts, backoff | `invalid_grant` → `expired`; `invalid_client`/`unauthorized_client` → incident (06.2.3-T3) |
| `GET channels.list?part=snippet&mine=true` | callback, and 06.4.2-T1 (refresh) | 10 s [INFERENCE] | 2 attempts on 5xx/transport, never on 401 | 401 → 302 `oauth_exchange_failed` (no row created); 5xx after retries → 302 `oauth_exchange_failed` |
| `IEmailSender.QueueAsync` | callback transaction | in-process | — | the callback still succeeds when the queue fails → wrap in a logged non-fatal path is **not** allowed (A8 commits in the same transaction); a queue failure rolls the connect back and answers `oauth_exchange_failed` so the user retries, because the connect e-mail is the anti-silent-publish control (D7) |
| `IEmailDeliveryStatus` / `SendEmailJob` | 09.2.6-T3 (Worker, queue `email`, 5 retries) | — | 5 | delivery state readable by the user's own row (tenant filter) |

Google HTTP access goes through `GoogleYouTubeClient` only, with base URLs from `YouTubeOptions` (`AuthorizationEndpoint`, `TokenEndpoint`, `RevokeEndpoint`, `ApiBaseUrl`) so `FakeGoogleServer` can replace them (D19). No other component calls Google.

**Access logs (YR23).** The load-balancer access log for `/api/v1/channels/*/callback` drops the query string (11.2.2-T1 note, recorded as **06 CR-10**); the app-side redaction of `code`/`state` is 09.9.3-T2 and is the second line, not the only one. This folder's routes contribute no other query-bearing URL, and the `__Host-nts_oauth` nonce is a cookie, never a query value.

**Operational note (staging).** A Testing project issues 7-day refresh tokens (G10, A11): a staging connection will go `expired` weekly and 06.4.2-T1 will then raise a deletion request — expected, not a bug. Staging re-connects weekly; prod (In production) is unaffected.

## 8. Security and privacy

| Control | Implementation |
|---|---|
| Tokens at rest | `ITokenProtector.Protect("{connectionId}", …)` → purpose `tokens.v1/{connectionId}` (A4/D4); the connection id is a `Guid.CreateVersion7()` generated before protection |
| PKCE verifier at rest | same port, purpose `tokens.v1/oauth.v1`; never a second protector and never `IDataProtectionProvider` directly (that root belongs to the TOTP store, A4/WT43) |
| State | 32 random bytes, stored as `ITokenIssuer.HashToken` SHA-256 hex (A9), single use, 10 min, `expires_at > now()` checked **inside** the claiming UPDATE |
| CSRF / callback authentication | the callback carries no bearer token; it is authenticated by three bindings on the same row — the single-use `state` (atomic claiming UPDATE committed before any Google call, README D3/F01), the browser nonce cookie `__Host-nts_oauth` compared with `CryptographicOperations.FixedTimeEquals` against `oauth_states.browser_nonce_hash` (**YR10**: a connect URL cannot be completed in another browser), and the session family (`refresh_family_id`) re-read through `IRefreshTokenService.GetReauthStateAsync` (no second session mechanism). `RequireSameOriginFilter` (cookie-auth POSTs, 09.3.2-T1) is not applicable: the connect POST is bearer-authenticated, and `SameSite=Lax` (required — the callback is a cross-site top-level GET) is what lets the nonce accompany it |
| Re-auth and legal gates | UC1 steps 4–5, exact codes (A5/A7) |
| Redirect safety | two fixed relative paths, an allow-listed `error` value from a closed set; `code`/`state` are never echoed; `Referrer-Policy: no-referrer` + `Cache-Control: no-store` (D3) |
| Least privilege | scopes `youtube.upload` + `youtube.readonly` only (A11/G12); the OAuth client is confidential and its secret lives only in the secret manager under `nts/<env>/api/YOUTUBE_CLIENT_SECRET` (A3); no Google password is ever handled |
| Tenant isolation | `ChannelConnection : IUserOwned`; another user's channel is a 404; the insert-conflict path relies on the DB index (`23505`), never on a cross-tenant existence query |
| Logs / audit | logs carry ids, statuses and codes only: never `code`, `state`, the `__Host-nts_oauth` nonce, a token, a verifier, a channel title or a client secret; the connect e-mail carries the channel title, time and device but no token (D17, 09.9.3-T2 redaction is the second line, and per **YR23** the LB access log for the callback path drops the query string — 06 CR-10) |
| Failure oracles | `oauth_state_invalid` is one code for unknown/expired/used/foreign-family states; `TryUnprotect == false` maps to the same code, never a stack trace (A4) |
| Exception safety | a Google failure never surfaces `WebException`/`HttpRequestException` text; the classifier (06.2.3-T3) and the callback map to fixed codes; no secret appears in an error message (A12) |

## 9. Test plan

Projects (09 README D2): `tests/NightTales.Domain.UnitTests`, `tests/NightTales.Application.UnitTests`, `tests/NightTales.Infrastructure.UnitTests`, `tests/NightTales.Api.IntegrationTests`, `tests/NightTales.TestKit`. xunit.v3 + Shouldly, `FakeClock`, integration on real PostgreSQL via Testcontainers; **no mocking library**.

`NightTales.TestKit` additions (owner 06.1.2-T1): `Google/FakeGoogleServer.cs` (Kestrel, D19 — this folder contributes the `/oauth2/v2/auth` shape check, `POST /token` (code + refresh, `invalid_grant`, `invalid_client`, `access_denied`, one-shot codes), `POST /revoke`, `GET /youtube/v3/channels?mine=true`, and the raw-body recorder the upload half extends), `Google/FakeGoogleOptions.cs`, plus `Email/FakeSmtpEmailSender` reuse (09.2.6-T3).

Folder-level assertions (each task file owns its exact test-case list and file paths):

- **Flags and gates** (`NightTales.Api.IntegrationTests`): flag off → 404 `feature_disabled` with no row; no re-auth → 401 `reauth_required`; no legal acceptance → 409 with `errors.documents`; each failure writes nothing.
- **Authorization URL** (`NightTales.Api.IntegrationTests`): `code_challenge_method=S256`, `access_type=offline`, `prompt=consent`, `include_granted_scopes=false`, exactly the two scopes, the exact `redirect_uri`, the plaintext state stored nowhere.
- **Browser binding** (`NightTales.Api.IntegrationTests`): connect sets `__Host-nts_oauth` with `HttpOnly`, `Secure`, `SameSite=Lax`, `Path=/` and a 600 s lifetime, and the nonce appears in no response body and no log; a callback without the cookie, or with a cookie minted by a different connect, answers the same `?error=oauth_state_invalid` with no exchange, and the cookie is cleared on every outcome (YR10).
- **Callback** (`NightTales.Api.IntegrationTests`): valid → 302 `/channels?connected=youtube` + one exchange + one `active` row + one audit row + one outbox row + one `email_messages` row; replay/expired/revoked-family/random state → the same `?error=oauth_state_invalid` with no second exchange; `access_denied` → `?error=oauth_denied`; `invalid_client` → `?error=oauth_exchange_failed` with the state spent **and** no connection/audit/outbox/e-mail row (the two-unit design); two concurrent callbacks → exactly one exchange; the exchange always uses the client pinned on the state row.
- **One owner per channel** (`NightTales.Api.IntegrationTests`): second user → `?error=channel_already_connected` with the victim row untouched; after a revoke the second user succeeds; concurrent connects → one row, no 500.
- **Crypto and secrets** (`NightTales.Api.IntegrationTests` + `NightTales.Infrastructure.UnitTests`): tokens unprotect only under `tokens.v1/{connectionId}`; `oauth.v1` ciphertext unprotects only under `tokens.v1/oauth.v1`; captured logs contain no token, code, state, channel title or address.
- **Tenant and shape** (`NightTales.Api.IntegrationTests` + `NightTales.Architecture.Tests`): `GET /channels` returns only the caller's rows in one SQL statement and no token/`oauth_client_id` member.
- **Domain** (`NightTales.Domain.UnitTests` + `NightTales.Application.UnitTests`): state single-use and 10-minute expiry across a DST fall-back on `FakeClock`; `Reconnect` keeps the row id and the client id; `Revoke` nulls both tokens; the audit projection carries ids only.
- **E-mail** (`NightTales.Api.IntegrationTests` + 09.2.6-T3's harness): one `channel_connected` row in the user's locale, `params` token-free, `sent` nulls `params`.
- **Migrations** (`NightTales.Infrastructure.UnitTests`): `dotnet ef migrations script` for `_06_OAuthStates`/`_06_ChannelConnections` contains exactly the two tables, their indexes and CHECKs; no pending model changes.

Manual staging check (D19): connect a real test channel (private only), confirm the e-mail in Mailpit/the real inbox, confirm the DB holds ciphertext, then 06.4.1-T1 revokes it at Google.

## 10. Tasks and re-estimate

| Task | Title | Size | Depends on | Blocks |
|---|---|---|---|---|
| 06.1.1-T1 | Google Cloud projects, consent screens, OAuth clients (staging + prod) | S (0.5) | 10.3.1-T1 (soft), 13.1.1-T1 (soft), 13.1.2-T1 (soft) | 13.4.1-T1 (13 CR-17) |
| 06.1.2-T1 | Connect + callback: state/PKCE store, re-auth, legal gate, token exchange | M (1.0) | 06.1.1-T1 (soft), 09.3.2-T1, 09.10.4-T1, 09.9.4-T1, 09.9.2-T2, 13.2.1-T1 | 06.1.2-T2, 10.7.1-T1 (10 CR-15), 07.1.2-T1 (07 CR-11), 10.7.1-T0 |
| 06.1.2-T2 | Channel fetch, `ChannelConnection` aggregate, pinned client resolver, connect e-mail, channel list | M (1.0) | 06.1.2-T1, 09.2.6-T3 | 06.2.1-T1, 06.2.3-T1, 06.4.1-T1, 10.7.1-T1 (10 CR-15), 08.4.1-T1 (08 CR-5), 07.1.2-T1, 07.1.2-T2 (07 CR-11), 06.1.2-T3 (06 CR-18) |

The header cells of the three task files repeat these rows verbatim (`| Story | Size | Area | Depends on | Blocks | Phase / flag |` in each file, cell 1 = `06.1.1`/`06.1.2`). Folder total **2.5 d**, equal to README §9's "06.1 setup + connect 2.5" (2.0 high-level + 0.5 for the P half of 06.1.2-T3 taken by 06.1.2-T2, F19). No re-estimate change is requested by this folder.

Ordering inside the folder is strict: 06.1.2-T1 needs the client ids and both endpoints (06.1.1-T1 soft — its acceptance run needs the real project, development can use a local redirect URI + `FakeGoogleServer`); 06.1.2-T2 needs the exchanged tokens and the channel fetch from 06.1.2-T1. Both task files' `Depends on` cells are the README §8.1 rows and are not rewritable here.

## 11. Rollout

1. **Before any deploy.** 06.1.1-T1 on staging first: separate Google Cloud projects (G10), consent screen with the privacy/terms URLs from 13.1.1-T1/13.1.2-T1 (soft) rendered by 10.3.1-T1, redirect URI `https://<staging origin>/api/v1/channels/youtube/callback`, both scopes only, the owner listed as a test user. Prod project "In production" unverified (G2 → private-only uploads, which is exactly C4's pre-audit mode). Client secrets go to the secret manager as `nts/<env>/api/YOUTUBE_CLIENT_SECRET` (A3/A12) — never into the repo, never into an env file.
2. **Deploy order.** Migrations first (`_06_OAuthStates`, `_06_ChannelConnections` are expand-only, safe to apply before the code), then Api (the callback and both routes live there; the Worker has no part in connect). `YouTube:ActiveClientId` must be set or startup validation fails (D11).
3. **Flag.** `youtube_publish` ships **off**: both routes answer 404 `feature_disabled` (A6). Turn it on for the owner only (P has one user; the S-phase rollout rules are 06.5.2-T1).
4. **Staging smoke (gate `06-connect`, README §7).** Re-auth → legal acceptance → connect → Google consent → callback → one `channel_connections` row `active` with ciphertext, `GET /channels` shows it, the connect e-mail arrives in the configured SMTP sink, then 06.4.1-T1 disconnects and Google revoke succeeds. Re-run the callback URL twice: the second must redirect `?error=oauth_state_invalid` and change nothing.
5. **Prod.** After the staging smoke: prod project, prod redirect URI, prod secret, flag on for the owner. Expect the first upload to be `private` until the audit (13.4.2-T1) — that is the pre-audit path (C4), not a bug of this folder.
6. **Rollback.** Turn `youtube_publish` off (routes 404 within 30 s, A6): no code rollback, no data unwind, no second mechanism. Existing connections stay valid for the S-phase multi-client work (06.1.2-T3). Expired `oauth_states` rows are inert (10 min) and are purged on the user's next connect.
7. **Secret rotation.** `YOUTUBE_CLIENT_SECRET` rotation keeps the same client id → existing connections keep refreshing (D5); only a new **client id** needs the S-phase settings path and the removal guard (06.1.2-T3).
