# 02.5.1/02.5.2 — Sources and safe fetcher: detailed plan

> Refines: `../../02-story-writing.md` §4 Epic 02.5 (stories 02.5.1, 02.5.2), §5.7, §5.9, §7.
> Contract: `../README.md` (D8, D10, D11, D19, D20, D21; F02, F13, F14, F15, F29; CR-1, CR-4).
> Template: `../../../00-overview.md` §10.1 · Layout: `00-overview.md` §5.2, §6, §8.
> Legend: ✅ read from source/docs · ⚠️ unverified · [DECISION] · [OPEN] · [INFERENCE].
> Status: detailed plan, 2026-09-23. Implemented: 02.5.1-T1 (#119), 02.5.2-T1 (#121), 02.5.2-T2 (#131, develop `312db85`), 02.5.2-T3 (#136, develop `d35284e`). Next: 02.5.2-T4 (Session 12).

## 1. Scope

**In.** Story 02.5.1 (add / list / remove sources on an episode, `user_note` sources, the 10-per-episode cap and the 30-url-per-rolling-24 h cap) and story 02.5.2 (SSRF-safe server-side fetch, main-text extraction, the isolated `NightTales.Fetcher` host, the internal lease API that replaces the withdrawn `fetch` Hangfire queue). Both stories are **P** and belong to the `02-M1-research` gate.

| Story | High-level | This plan | Why |
|---|---|---|---|
| 02.5.1 Add sources | S | **M (1.0 d)** — 02.5.1-T1 | `story_sources` gains `normalized_url`, `registrable_domain`, `content_type`, `text_chars`, `note_text` (CR-4); the cap moves from "when a source is added" to a rolling-24 h count (F15); the fetch task row is inserted in the same transaction (D10) |
| 02.5.2 Safe fetch | M + S spike + S isolation | **4.0 d** — T0 (0.5) + T1 (1.0) + T2 (1.0) + T3 (1.0) + T4 (0.5) | F02 (the Hangfire-in-the-fetcher contradiction) is resolved by D10, which is a **new host + new internal API + new table**, not a configuration change; F14 turns §5.7 into a 19-row threat table plus a 37-case suite |

**Out.** Fact-sheet generation and approval (02.5.3/02.5.4/02.5.5). Similarity (02.5.6). The source *finder* (02.7) — this plan only reserves the `find_sources` task kind and the result shape in the lease API (D10), and owns the allow-list mechanism 02.7.2-T1 populates. Rendering, publishing, the UI implementation (10). No OpenAI call happens in this feature: fetching is free, so no `ICreditLedger` reservation and no `usage_records` row (D1, D14).

**Requirement ids touched.** FR-02-4 (≥ 2 fetched, cited sources), FR-02-8 (server-side, SSRF-safe, capped, isolated). NFR-02-4 (the fetcher must not become an egress path into the platform network). G7/G8 (why user-supplied links and not platform scraping). 13 (deletion with the episode, D19).

## 2. Confirmed facts

| # | Fact | Source | Use here |
|---|---|---|---|
| E1 | SmartReader **0.11.1** (2026-07-25), **Apache-2.0**, Readability port for netstandard2.0/2.1, depends on AngleSharp ≥ 1.5.2 ✅ | README §1 E1 | Candidate A for T0: in-process, no sidecar, no second runtime in the fetcher container |
| E2 | trafilatura **2.2.0** (2026-07-31), Apache-2.0 since v1.8.0, Python ≥ 3.10 ✅ | README §1 E2 | Candidate B for T0: better extraction quality, but adds a Python sidecar to a container whose whole point is a minimal attack surface (D10) and would break "all background work in C#" (F19 precedent) |
| E3 | AngleSharp **1.8.2** (2026-09-18), MIT ✅; NReadability abandoned since 2014, no license → rejected | README §1 E3 | AngleSharp is a transitive dependency only; we do not parse HTML ourselves |
| E4 | TurnerSoftware.RobotsExclusionTools 0.9.1, MIT, last release **2022-08-09**, predates RFC 9309 ⚠️ unmaintained | README §1 E4 | T0 writes a small in-house RFC 9309 subset spec instead of taking this dependency |
| D10 | Pull model: no DB, no Hangfire, no storage in the fetcher. `fetch_tasks` table, internal API on `:8081` with `FETCHER_API_TOKEN`, lease ≤ 4 platform-wide and 1 per registrable domain, 90 s lease, 60 s heartbeat, `attempts = 2` → `failed source_unreachable`, text ≤ 300,000 chars stored by the **Api** | README D10, F02, CR-1 | The whole shape of 02.5.2-T2/T3 |
| D11 | Fetch safety rules: scheme/port/userinfo/host rules, no IP literals, IANA global-unicast only, `ConnectCallback` re-check, manual redirects ≤ 3, decompressed cap, content-type sniff, robots RFC 9309 with 4xx→allow / 5xx→disallow, 1 req/s per host, `NightTalesStudioBot/<version>`, no JS, raw HTML never stored | README D11, F14 | The whole shape of 02.5.2-T1/T4 |
| D8 | Error catalogue: `source_url_invalid` 400, `source_domain_not_allowed` 400, `source_duplicate` 409, `source_limit_reached` 409, `source_fetch_limit_reached` 429, plus the job codes `source_unreachable`, `source_too_large`, `source_blocked_by_robots`, `source_unsupported_type`, `source_no_text` | README D8, F29 | The mapping table in §5.4 |
| D19 | Never log source text, titles or URLs; log ids, codes, latency and the **registrable domain**. Metrics `fetch_tasks_total{status,error_code}`, `fetch_duration_seconds` | README D19 | §8, §7.4 |
| D20 | Fetcher tests use an in-process Kestrel server on 127.0.0.1 **only** through a test-only `IAddressValidator`, plus `FakeDnsResolver`. Production code has no loopback bypass | README D20 | §9, and an architecture test |
| F13 | The true-crime minimum is ≥ 2 distinct `fetched` url sources each cited by ≥ 1 kept fact, with distinct `content_sha256` | README F13 | `story_sources.content_sha256` is what 02.5.4-T1 counts; this plan must therefore always persist it on success |
| F15 | The per-user cap is counted on `story_sources` creations in rolling 24 h; concurrency is enforced server-side by the lease endpoint (≤ 4) and client-side by the fetcher loop | README F15 | 02.5.1-T1 step 4, 02.5.2-T2 step 3 |
| P4/11 | 11.2.2-T2 owns the `fetcher` container + subnet + egress test; 11 §5.1 currently says "no DB credentials" and 09.2.6 says a `fetch` Hangfire queue — CR-1 removes the queue and keeps "no DB" | README P4, CR-1 | 02.5.2-T3 states the exact network contract 11.2.2-T2 implements |
| MPT | Irrelevant here: fetching and extraction never touch MPT, and the fetcher host holds no MPT adapter config | M1/M2/M3 | Keeps the fetcher container's secret set at exactly one token |

## 3. Domain and data

### 3.1 Domain (`src/NightTales.Domain/Research/`)

```csharp
// StorySource.cs — aggregate child of Episode (no independent aggregate root: it is always
// reached through the episode, matching 09's Episode aggregate).
public sealed class StorySource : Entity<Guid>
{
    public Guid UserId { get; private set; }
    public Guid EpisodeId { get; private set; }
    public StorySourceKind Kind { get; private set; }        // Url | UserNote
    public string? Url { get; private set; }                 // as pasted, ≤ 2048
    public string? NormalizedUrl { get; private set; }       // NormalizedUrl.Value
    public string? RegistrableDomain { get; private set; }   // public-suffix registrable domain
    public string? NoteText { get; private set; }            // ≤ 3000, UserNote only
    public StorySourceStatus Status { get; private set; }    // Pending | Fetched | Failed | Rejected
    public string? ErrorCode { get; private set; }
    public int? HttpStatus { get; private set; }
    public string? ContentType { get; private set; }
    public string? ContentSha256 { get; private set; }       // 64 lowercase hex
    public string? ExtractedTextKey { get; private set; }    // object-storage key, never a URL
    public int? TextChars { get; private set; }
    public string? FinalUrl { get; private set; }
    public string? Title { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset? FetchedAt { get; private set; }

    public static ErrorOr<StorySource> CreateUrl(Guid id, Guid userId, Guid episodeId, SourceUrl url, DateTimeOffset now);
    public static ErrorOr<StorySource> CreateNote(Guid id, Guid userId, Guid episodeId, string noteText, DateTimeOffset now);
    public void MarkFetched(string finalUrl, string? title, string contentType, string sha256,
                            string textKey, int textChars, DateTimeOffset at);   // → Fetched
    public void MarkFailed(string errorCode, int? httpStatus, DateTimeOffset at); // → Failed
    public void MarkRejected(string errorCode);                                   // → Rejected (blocked domain)
    public void Retry(DateTimeOffset now);                                        // Failed → Pending
}
```

```csharp
// SourceUrl.cs — value object; the only place URL syntax is decided.
public sealed record SourceUrl
{
    public string Value { get; }            // normalised: lowercase scheme+host, punycode, no fragment, no trailing dot
    public string Host { get; }             // punycode, no trailing dot
    public string RegistrableDomain { get; }
    public bool IsHttps { get; }
    public static ErrorOr<SourceUrl> Parse(string input, IPublicSuffixList psl);
}
// Rejects, with SourceErrors.SourceUrlInvalid in every case:
//   scheme ∉ {http, https}; explicit port ∉ {80, 443}; userinfo present; length > 2048;
//   host is an IP literal in ANY notation (dotted, decimal 2130706433, octal 0177.0.0.1, hex 0x7f.0.0.1,
//   IPv6 in brackets, IPv4-mapped IPv6); empty host; host has no registrable domain (e.g. "localhost",
//   a single-label name, or a bare TLD); unparsable.
// Normalises: IDNA/punycode, lowercase, strip one trailing dot, drop the fragment, drop a default port,
//   collapse an empty path to "/". A trailing dot is stripped *before* the single-label check.
```

```csharp
// FetchTask.cs — the unit of work the fetcher leases.
public sealed class FetchTask : Entity<Guid>
{
    public Guid UserId { get; private set; }
    public Guid? EpisodeId { get; private set; }
    public Guid? SourceId { get; private set; }             // null for FindSources
    public FetchTaskKind Kind { get; private set; }         // FetchSource | FindSources
    public string? Url { get; private set; }                // FetchSource only
    public string? RegistrableDomain { get; private set; }  // FetchSource only; drives 1-lease-per-domain
    public FetchTaskStatus Status { get; private set; }     // Queued | Leased | Done | Failed | Cancelled
    public int Attempts { get; private set; }
    public DateTimeOffset NotBefore { get; private set; }   // backoff between attempts
    public DateTimeOffset? LeaseUntil { get; private set; }
    public string? LeasedBy { get; private set; }           // fetcher instance id
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset? FinishedAt { get; private set; }
    public string? ErrorCode { get; private set; }

    public bool IsLeaseExpired(DateTimeOffset now) => Status == FetchTaskStatus.Leased && LeaseUntil <= now;
    public void Lease(string workerId, DateTimeOffset now, TimeSpan lease); // Queued → Leased, Attempts++
    public void Heartbeat(DateTimeOffset now, TimeSpan lease);
    public void Complete(DateTimeOffset now);                               // → Done
    public void Fail(string errorCode, DateTimeOffset now);                 // → Failed
    public void Requeue(DateTimeOffset now, TimeSpan backoff);              // expired lease → Queued
    public void Cancel(DateTimeOffset now);
}
```

`DomainBlocklist` (Domain, pure): `bool IsBlocked(string registrableDomain)` over an injected `IReadOnlySet<string>` — matching is on the registrable domain **and** every suffix of the host, so `m.youtube.com` and `youtube.com` both hit the `youtube.com` entry. The set is data (`Fetcher:BlockedDomains`), validated at start-up by `FetcherOptionsValidator` (fail boot on an entry that is not a registrable domain, is an IP literal, or has a scheme/path). Two layers use it: the Application command (rejects early, 400 `source_domain_not_allowed`, status `Rejected`) and the fetcher at every hop (D11: redirects).

### 3.2 Persistence (`src/NightTales.Infrastructure/Persistence/`)

`story_sources` (extends 00 §6; CR-4 adds the marked columns):

| Column | Type | Notes |
|---|---|---|
| `id` | `uuid` PK | |
| `user_id`, `episode_id` | `uuid` NOT NULL | FK → `episodes` `ON DELETE CASCADE` (D19: deleted with the episode) |
| `kind` | `text` NOT NULL | `url` \| `user_note` |
| `url` | `text` NULL | as pasted; `CHECK (kind <> 'url' OR url IS NOT NULL)` |
| `normalized_url` | `text` NULL | **new** (CR-4); `CHECK (char_length(normalized_url) <= 2048)` |
| `registrable_domain` | `text` NULL | **new** (CR-4) |
| `final_url`, `title` | `text` NULL | from the fetch result |
| `note_text` | `text` NULL | **new** (CR-4); `CHECK (kind <> 'user_note' OR (url IS NULL AND note_text IS NOT NULL))` and `CHECK (note_text IS NULL OR char_length(note_text) <= 3000)` |
| `status` | `text` NOT NULL | `pending` \| `fetched` \| `failed` \| `rejected` |
| `error_code` | `text` NULL | D8 job codes |
| `http_status` | `int` NULL | |
| `content_type` | `text` NULL | **new** (CR-4) |
| `content_sha256` | `char(64)` NULL | **new** (CR-4); F13 counts distinct values |
| `extracted_text_key` | `text` NULL | object-storage key |
| `text_chars` | `int` NULL | **new** (CR-4); ≤ 300,000 |
| `created_at`, `fetched_at` | `timestamptz` | |

Indexes: `UNIQUE (episode_id, normalized_url) WHERE kind = 'url'` (→ 409 `source_duplicate`); `(episode_id, status)`; `(episode_id, created_at)`.

`source_creations` (02 CR-31, F15): `id uuid` PK, `user_id uuid` NOT NULL (FK → users `RESTRICT`), `created_at timestamptz` NOT NULL; index `(user_id, created_at)` is the rolling-24 h cap query. Append-only, one row per url add and per retry, written in the command's transaction; no URL, note, episode or source reference (D13/D19), so deleting a source or an episode frees no quota. The 13.3.1 deletion engine deletes a user's rows by `user_id`.

`fetch_tasks` (D10 + two additions, see [OPEN] 1 in §10):

| Column | Type | Notes |
|---|---|---|
| `id` | `uuid` PK | |
| `user_id` | `uuid` NOT NULL | |
| `episode_id` | `uuid` NOT NULL | FK `ON DELETE CASCADE`; **[DECISION T38]** a `find_sources` task is episode-level too, so this is NOT NULL for every kind |
| `source_id` | `uuid` NULL | FK → `story_sources` `ON DELETE CASCADE`; null for `find_sources` |
| `generation_run_id` | `uuid` NULL | **addition 3** (T38) — the `find_sources` run whose payload is re-derived at lease time; null for `fetch_source` |
| `kind` | `text` NOT NULL | `fetch_source` \| `find_sources` |
| `url` | `text` NULL | **addition 1** — D10's lease response returns `url`, so the row must carry it |
| `registrable_domain` | `text` NULL | **addition 2** — required to enforce "1 lease per registrable domain" without a join |
| `status` | `text` NOT NULL | `queued` \| `leased` \| `done` \| `failed` \| `cancelled` |
| `attempts` | `int` NOT NULL DEFAULT 0 | |
| `not_before` | `timestamptz` NOT NULL DEFAULT now() | |
| `lease_until`, `leased_by` | `timestamptz` / `text` NULL | |
| `leased_at` | `timestamptz` NULL | **addition 4** (T38) — set on lease, cleared on requeue; makes `fetch_duration_seconds` measure fetch time, not queue time |
| `error_code` | `text` NULL | |
| `created_at`, `finished_at` | `timestamptz` | |

Indexes: `(status, not_before)` (D10, the lease scan); `UNIQUE (source_id) WHERE source_id IS NOT NULL AND status IN ('queued','leased')` (one live task per source); `(status, registrable_domain) WHERE status = 'leased'`; `(status, lease_until) WHERE status = 'leased'` (the sweeper).

Migration `20261005_SourcesAndFetchTasks` (single migration, both tables + indexes + checks). The `find_sources` requester payload is **not** stored in `fetch_tasks`; it is re-derived by the lease endpoint from the `generation_runs` row of the `find_sources` run (§5.3) — no duplicated brief text in a table the fetcher can see.

### 3.3 Config keys (owned here; defaults are D11 values)

| Key | Default | Meaning |
|---|---|---|
| `Sources:MaxPerEpisode` | `10` | hard cap → 409 `source_limit_reached` |
| `Sources:MaxUrlSourcesPerUserPerDay` | `30` | rolling 24 h → 429 `source_fetch_limit_reached` |
| `Sources:MaxNoteChars` | `3000` | `user_note` limit |
| `Fetcher:BlockedDomains` | the 19 entries from D11 (02.5.2-T4 corrected the stale "20") | data, PR-reviewed |
| `Fetcher:UserAgent` | `NightTalesStudioBot/0.1.0 (+https://{PublicHost}/bot)` | `Fetcher:PublicHost` required |
| `Fetcher:MaxDecompressedBytes` | `2097152` | counting stream, gzip/br |
| `Fetcher:TotalTimeoutSeconds` / `RobotsTimeoutSeconds` | `10` / `5` | per page / per robots fetch |
| `Fetcher:ExtractTimeoutSeconds` / `ExtractMaxElements` / `ExtractMaxDepth` | `5` / `50000` / `512` | T105 (SEC-0928-1): the SmartReader parse is synchronous and outside the request budget — a dedicated-worker watchdog bounds its time; the pre-parse guard (AngleSharp's non-recursive `HtmlParser` with an element/depth abort during the parse and an iterative tree walk (02.5.2-T1 review F1)) bounds elements and nesting depth → `source_too_large`; extract watchdog timeout → `source_no_text` (`extract_timeout`) |
| `Fetcher:MaxRedirects` | `3` | manual |
| `Fetcher:MaxTextChars` | `300000` | result validation; also the truncation point is a **failure**, not a truncation |
| `Fetcher:RobotsCacheHours` | `24` | in-memory per host |
| `Fetcher:PerHostRequestsPerSecond` | `1` | fetcher-side floor; the effective interval is `max(1 s, robots crawl-delay)` (T43) |
| `Fetcher:CrawlDelayMaxSeconds` | `10` | a robots `crawl-delay` above this → `source_blocked_by_robots` (T43) |
| `Fetcher:LeaseSeconds` / `HeartbeatSeconds` | `90` / `60` | D10 |
| `Fetcher:SweepIntervalSeconds` | `60` | `FetchLeaseSweeperJob` |
| `Fetcher:MaxAttempts` | `2` | then `failed source_unreachable` |
| `Fetcher:MaxLeasesPlatformWide` / `PerDomain` | `4` / `1` | D10, FR-02-8 |
| `Fetcher:Internal:ListenPort` | `8081` | Api side; bound only on the internal interface |
| `Fetcher:Internal:BaseUrl` | `http://api:8081` | fetcher side |
| `Fetcher:InstanceId` | host name | `leased_by` |

All of `FetcherOptions` is validated with `ValidateOnStart()`: a bad blocked-domain entry, a UA without a contact URL, a cap of 0 or a `MaxDecompressedBytes` above 8 MiB fails boot. The **fetcher** host validates the same file but only the keys it needs; the Api validates the blocklist and the caps it enforces.

## 4. Use cases

| # | Use case | Handler | Notes |
|---|---|---|---|
| UC1 | Add a url source | `AddSourceCommand(episodeId, url, noteText?)` → `AddSourceHandler` | state gate `draft`/`story_review` else 409 `episode_invalid_state` (F26 precedent); blocklist → 400 `source_domain_not_allowed`; parse → 400 `source_url_invalid`; episode cap → 409 `source_limit_reached`; 24 h cap → 429 `source_fetch_limit_reached`; duplicate → 409 `source_duplicate`; else one transaction: `story_sources` (`pending`) + `fetch_tasks` (`queued`); `[Audited("episode.source_added", Category.Episodes, Visibility.User)]`; returns `SourceDto` |
| UC2 | Add a note source | same handler, `noteText` set and `url` null | no `fetch_tasks` row. [DECISION T41] A `user_note` row is created `Fetched` with `content_sha256 = sha256(UTF8(NFC(note_text)))`, `text_chars = noteText.Length`, `extracted_text_key = null` (the text lives in the row, ≤ 3,000 chars). 02.5.3-T1 reads note text from the column, url sources from storage; 02.5.4-T1 counts only `kind = 'url'` toward FR-02-4 |
| UC3 | List sources | `ListSourcesQuery(episodeId)` → `IReadDb` projection | returns `id, kind, url, title, status, errorCode, httpStatus, textChars, fetchedAt, isCited` (⚠️ `isCited` needs 02.5.4's fact sheet: 02.5.4-T1 fills it; here it is `false`) |
| UC4 | Remove a source | `RemoveSourceCommand(episodeId, sourceId)` | deletes the row (cascade deletes the `fetch_tasks` row); a `leased` task is cancelled through the cascade; `[Audited("episode.source_removed")]`; 404 if not on the episode; blocked in `story_approved`/`rendering` → 409 `episode_invalid_state` |
| UC5 | Retry a failed source | `RetrySourceCommand(episodeId, sourceId)` | `failed` → `pending` + a new `fetch_tasks` row; counts against the 24 h cap |
| UC6 | Lease tasks | `LeaseFetchTasksCommand(workerId, max)` | `FOR UPDATE SKIP LOCKED`, caps 4 / 1 per domain |
| UC7 | Heartbeat | `HeartbeatFetchTaskCommand(taskId, workerId)` | `+60 s`, only while `leased` by that worker |
| UC8 | Complete | `CompleteFetchTaskCommand(taskId, workerId, FetchTaskResult)` | validates, stores text, updates the source, raises `SourceFetched` |
| UC9 | Sweep expired leases | `FetchLeaseSweeperJob` | `attempts < 2` → requeue with `not_before = now + 30 s`; else `failed source_unreachable` |

`AddSourceHandler` order matters: validate syntax → blocklist → **episode cap** → **24 h cap** → duplicate. The caps are checked inside the transaction with a `SELECT count(*)` under the episode row lock (the episode aggregate is loaded tracked), so two parallel adds cannot both pass the 10th slot.

## 5. API and events

### 5.1 Public routes (Api `:8080`, user JWT, in OpenAPI)

| Method | Route | Body / result | Codes |
|---|---|---|---|
| `GET` | `/api/v1/episodes/{id}/sources` | `SourceDto[]` + `limits { maxPerEpisode, urlSourcesLast24h, maxUrlSourcesPerDay }` | 200, 404 |
| `POST` | `/api/v1/episodes/{id}/sources` | `{ "url": "https://…" }` or `{ "noteText": "…" }` (exactly one) | 201, 400 `source_url_invalid` / `source_domain_not_allowed`, 409 `source_duplicate` / `source_limit_reached` / `episode_invalid_state`, 429 `source_fetch_limit_reached` (+ `Retry-After`) |
| `DELETE` | `/api/v1/episodes/{id}/sources/{sourceId}` | — | 204, 404, 409 `episode_invalid_state` |
| `POST` | `/api/v1/episodes/{id}/sources/{sourceId}/retry` | — | 202, 409, 429 `source_fetch_limit_reached` |

`201` body: `{ "id": "…", "kind": "url", "url": "https://…", "normalizedUrl": "https://…", "registrableDomain": "example.com", "status": "pending", "createdAt": "…" }`. The `url` returned is the **normalised** one; the pasted one is only in the request. `Retry-After` for the 24 h cap = seconds until the oldest counted row leaves the window.

### 5.2 Internal routes (Api `:8081`, bearer `FETCHER_API_TOKEN`, **not** in the public OpenAPI, no user JWT)

Separate Kestrel endpoint (port 8081 on `Fetcher:Internal:ListenAddress`, default `127.0.0.1`, `0.0.0.0` only in containers, 11 CR-16), a dedicated `FetcherTokenAuthenticationHandler`, constant-time comparison (`CryptographicOperations.FixedTimeEquals`), its own rate limiter (60 req/min per fetcher instance id), and a separate endpoint group that is never registered on the public listener. Missing/incorrect token → 401 with no body detail (never echo the token, never log it — D19).

```jsonc
// POST /internal/fetcher/v1/tasks/lease      { "workerId": "fetcher-1", "max": 4 }
[
  { "taskId": "3f…", "kind": "fetch_source", "url": "https://example.com/a",
    "attempt": 1, "leaseUntil": "2026-09-23T10:00:90Z",
    "userAgent": "NightTalesStudioBot/0.1.0 (+https://nts.example/bot)" },
  { "taskId": "40…", "kind": "find_sources", "attempt": 1, "leaseUntil": "…", "userAgent": "…",
    "finderRequest": { "topic": "true_crime", "language": "vi", "seriesTitle": "…",
                       "brief": "…", "allowList": ["vnexpress.net", "tuoitre.vn"], "maxCandidates": 10 } }
]
```

```jsonc
// POST /internal/fetcher/v1/tasks/{taskId}/heartbeat  { "workerId": "fetcher-1" }
//   → 200 { "leaseUntil": "…" } | 409 { "code": "fetch_lease_lost" } (the worker drops the task)
// POST /internal/fetcher/v1/tasks/{taskId}/result
// [DECISION T39] every result body carries `status`: "done" (work completed, data present) or "failed" (a D8 job code).
{ "workerId": "fetcher-1", "status": "done", "httpStatus": 200,
  "finalUrl": "https://example.com/a", "title": "…", "contentType": "text/html; charset=utf-8",
  "text": "…", "textSha256": "…" }            // ≤ 300,000 chars, UTF-8
{ "workerId": "fetcher-1", "status": "failed", "errorCode": "source_blocked_by_robots" }
{ "workerId": "fetcher-1", "status": "done", "candidates": [ { "url": "…", "title": "…", "site": "vnexpress.net" } ] }
//   → 200 { "taskId": "…", "status": "done" } | 409 { "code": "fetch_lease_lost" }
// For a `fetch_source` task, `status: "done"` requires the text fields; for a `find_sources` task it requires `candidates`.
```

Result validation (all → 400 with a code, the task stays `leased` and expires normally):
`text` missing/empty for a `fetch_source` `status: "done"` → `source_no_text`; `textSha256` ≠ `sha256(UTF8(text))` → `source_no_text` (treat a mismatch as corruption, never store it); `textChars > 300,000` → `source_too_large`; `finalUrl` fails `SourceUrl.Parse` **or** its registrable domain is blocked → `source_domain_not_allowed`; unknown `errorCode` on a `status: "failed"` body → `source_unreachable`; `candidates` for a `find_sources` task with a URL that fails `SourceUrl.Parse` are dropped silently (the user re-adds them through 02.5.1, which re-validates anyway).

**[DECISION T39] `status` is always present**: `"done"` (with the text fields for `fetch_source`, or with `candidates` for `find_sources`) or `"failed"` (with a D8 job code). Resolved; see §10.

### 5.3 Storage of extracted text (owned by the Api, D10)

`IExtractedTextStore` (`IBlobStore` wrapper, Infrastructure/Storage): key `research/{userId}/{episodeId}/{sourceId}/{sha256}.txt`, content `text/plain; charset=utf-8`, body = the text exactly as validated. Written **before** the row update, so a crash between the two leaves an orphan object (cleaned by 13.3's deletion sweep) and never a row pointing at a missing object. The key is content-addressed, so a retry that produces the same text writes the same object. `content_sha256` is stored on the row (F13) and is what 02.5.4-T1 counts for the "≥ 2 distinct sources" rule.

### 5.4 Error mapping (D8, one table so the fetcher, the API and the UI never disagree)

| Situation | Where | Code | HTTP | `story_sources.status` |
|---|---|---|---|---|
| scheme/port/userinfo/IP-literal/length/unparsable/IDN-to-single-label | AddSourceHandler, each redirect hop, result validation | `source_url_invalid` | 400 | `rejected` (on add) |
| blocklisted domain (initial or any redirect hop) | AddSourceHandler + fetcher | `source_domain_not_allowed` | 400 | `rejected` / `failed` (hop) |
| same normalised URL on the episode | AddSourceHandler | `source_duplicate` | 409 | — |
| 11th source on the episode | AddSourceHandler | `source_limit_reached` | 409 | — |
| 31st url source in 24 h | AddSourceHandler | `source_fetch_limit_reached` | 429 | — |
| DNS failure, connect failure, timeout, > 3 redirects | fetcher | `source_unreachable` | job only | `failed` |
| decompressed body > 2 MB, or result `textChars` > 300,000 | fetcher / Api | `source_too_large` | job only | `failed` |
| robots 5xx/unreachable, or robots disallows the path | fetcher | `source_blocked_by_robots` | job only | `failed` |
| content type ∉ {`text/html`, `text/plain`} after sniff | fetcher | `source_unsupported_type` | job only | `failed` |
| extraction yielded < 200 chars (SPA, paywall, empty) | fetcher | `source_no_text` | job only | `failed` |
| lease expired twice (`attempts = 2`) | sweeper | `source_unreachable` | job only | `failed` |
| episode not in `draft`/`story_review` | AddSourceHandler | `episode_invalid_state` (09) | 409 | — |

Job codes are **never** an HTTP response (D8) and never a `ProblemDetails`; the UI reads them from `GET /episodes/{id}/sources`.

### 5.5 Events

`SourceFetched { SourceId, EpisodeId, UserId, OccurredAt }` (as built by 02.5.2-T2: the event keeps its four existing fields because consumers re-read the episode; see that task's §Notes [DECISION]) and `SourceRemoved { … }` (Domain `Research/`), raised by the Api handler (and by `FetchLeaseSweeperJob` when it fails a source), written to `outbox_messages` in the same transaction, dispatched to SignalR `EpisodeUpdated` (09.5.3, no new event type — CR-7 shape only carries `generation`). No OpenAI call, no `generation_runs` row, no credit reservation: fetching is free (D1/D14).

## 6. UI

Owned by 10; this plan fixes the contract only. Episode detail (`10.5`, ⚠️ confirm the exact screen id with 10) gets a **Sources** panel:

| Element | Behaviour |
|---|---|
| Add row | one input, paste a URL, or switch to "my note" (textarea, counter `n/3000`) |
| Source list | one row per source: title or host, kind badge ("your note" for `user_note`), status chip `pending` (spinner) / `fetched` / `failed` / `rejected` |
| `failed` row | the human message for the code + **Retry** (429-aware: shows the wait when the cap is hit) |
| `rejected` row | the D8 message for `source_domain_not_allowed`: "Paste an article or record page; video and social platforms cannot be used as sources." |
| Counters | `n/10 sources`; `m/30 url sources today` (from the `limits` block) |
| Empty state | explains the true-crime minimum (≥ 2 fetched, cited sources) |
| Forbidden | never render the extracted text in the panel in P (it is long and only the fact sheet matters); 10 may add a preview behind a click later [OPEN] |

No new route is needed for the UI beyond §5.1; no SignalR event type is added.

## 7. Jobs and integrations

### 7.1 The withdrawn Hangfire queue (F02, CR-1)

09.2.6's `fetch` queue and 11 §5.1's "fetcher runs Hangfire" are **removed** by CR-1. There is no `FetchSourceJob` class, no `fetch` queue name, and no Hangfire dependency in the fetcher container. The queue is replaced by `fetch_tasks` + the lease API. 02.5.2-T2/T3 carry the CR-1 text to 09/11; this folder never edits those files.

### 7.2 `NightTales.Fetcher` (new project `src/NightTales.Fetcher/`)

.NET 10 console (`Microsoft.NET.Sdk`, `OutputType=Exe`), `Host.CreateApplicationBuilder` + one `BackgroundService` (`FetcherLoop`) + a typed `HttpClient` for the internal API. No EF, no Hangfire, no OpenAI SDK, no `IBlobStore` — the project references only `NightTales.Domain` (for `SourceUrl`, `DomainBlocklist`, `SourceErrors`) and a small `NightTales.Infrastructure`-free set of fetcher classes. **[DECISION T40, accepted]** `SafeHttpSourceFetcher` and the safety classes live in a new library `src/NightTales.Fetcher.Core/` (Domain only) referenced by both `NightTales.Fetcher` and `NightTales.Infrastructure`, because 00 §5.2's "Infrastructure/Research" placement would drag EF Core, the OpenAI SDK, Hangfire and the storage client into a container that D10/11 §5.1 requires to have none of them. 00 §5.2 gains the layout row.

Loop, per iteration (D11 + F15):

1. `lease` with `max = 4` (the loop keeps its own in-flight count and requests `4 − inFlight`); empty → sleep 2 s (jittered).
2. For each leased task, start a `Task` bounded by the semaphore of 4 and by `MaxLeasesPlatformWide` (server-authoritative — the client count is only an optimisation).
3. `fetch_source`: validate the URL again locally (`SourceUrl.Parse` + blocklist) → robots → per-host rate limit (a `KeyedRateLimiter<string>` at 1 req/s on the registrable domain) → fetch with `SafeHttpSourceFetcher` → extract → post the result. `find_sources`: hand to `ISourceFinderRunner` (02.7.2-T3 owns it; the fetcher only routes the task and posts candidates) — if the finder is not wired (flag off, 02.7 not shipped), the task is failed `find_sources_generation_failed` and no such task is ever created before 02.7 lands.
4. `heartbeat` every 60 s while a task runs (a `PeriodicTimer` per task). T105 (SEC-0928-1): for `fetch_source` the heartbeat self-disposes after `TotalTimeoutSeconds + ExtractTimeoutSeconds` (~15 s) — the fetch+extract window cannot exceed it, so a hostile page must not pin one of the ≤ 4 lease slots by holding heartbeats; only a `find_sources` task heartbeats past the bound.
5. On `409 fetch_lease_lost` at any point → abandon the work, drop the result, do not retry (the sweeper owns it).
6. Shutdown: cancel in-flight work, stop leasing; leases expire in ≤ 90 s and the sweeper requeues. No draining protocol.

### 7.3 Network contract for 11.2.2-T2 (02.5.2-T3 supplies this text)

| Rule | Value |
|---|---|
| Ingress to the fetcher container | none (no listening port) |
| Egress allowed | `0.0.0.0/0:80,443` (internet) **and** the Api internal listener `api:8081` |
| Egress denied | the app subnet (except `api:8081`), the render subnet, the `mpt-adapter` port, PostgreSQL, the object-storage endpoint, the cloud metadata address `169.254.169.254`, DNS other than the configured resolver |
| Secrets in the container | `FETCHER_API_TOKEN` only (+ `Fetcher:Internal:BaseUrl`) |
| Egress test (owned by 11.2.2-T2) | from the fetcher's network namespace (debug sidecar; 11 CR-10): `api:8081` reachable; `postgres:5432`, the storage endpoint, a render node, `169.254.169.254` all fail; a plain `https://example.com` succeeds |
| Image | the same hardened base as Api/Worker, non-root, read-only rootfs, no shell tools [INFERENCE — confirm the base-image convention with 11] |

### 7.4 Observability (D19)

Metrics: `fetch_tasks_total{status,error_code}` (counter, incremented on every terminal transition), `fetch_duration_seconds` (histogram, per task, from lease to result), `fetch_leases_active` (gauge), `fetch_robots_cache_entries`, `fetch_blocked_total{reason}`. Logs (Serilog, structured): task id, source id, episode id, kind, attempt, status, error code, http status, duration, **registrable domain only**, plus `extract_reason` when a `source_no_text`/`source_too_large` came from the extract phase (`extract_timeout` | `too_many_elements` | `too_deep`, T105). Never the URL path, query, title or text. `FETCHER_API_TOKEN` is never logged; the internal API's request logging excludes the `Authorization` header. Alerts (08.2): `fetch_tasks_total{status="failed"}` rate > 30 % over 15 min, and `fetch_leases_active == 0` for 10 min while `fetch_tasks` has `queued` rows (a dead fetcher).

## 8. Security and privacy

Threat model: the fetcher is the only component that makes outbound requests to arbitrary user-supplied URLs, so it is the platform's SSRF and abuse surface. It has no DB, no storage and no OpenAI credentials (D10), so a compromise yields only the network position — which is exactly what the table below removes.

| # | Threat | Vector | Control (D11) | Verified by |
|---|---|---|---|---|
| 1 | SSRF to loopback / private / link-local | `http://127.0.0.1:8080/…`, `http://10.0.0.1/`, `http://192.168.1.1/` | global-unicast-only validator at resolve **and** connect; no loopback bypass in production | 02.5.2-T4 cases 1–3 |
| 2 | Cloud metadata theft | `http://169.254.169.254/latest/meta-data/` | `169.254.0.0/16` blocked; the container has no metadata route | 02.5.2-T4 case 4 + 11.2.2-T2 egress test |
| 3 | IP-literal obfuscation | `http://2130706433/`, `http://0177.0.0.1/`, `http://0x7f.0.0.1/`, `http://[::ffff:127.0.0.1]/` | every host must be a DNS name — an IP literal in any notation is rejected before DNS | 02.5.2-T4 cases 5–9 |
| 4 | DNS rebinding (TOCTOU) | the target's DNS answers change between the policy decision and the connection | **one** resolution, performed inside `ConnectCallback`, then a connect to the validated `IPAddress` itself — the handler never re-resolves the host name, so the address that was checked is the address that is connected to. The `RemoteEndPoint` re-check is defence-in-depth, not the control | 02.5.2-T4 case 10 (`FakeDnsResolver`: a host the fake resolves to a public address must produce **zero** connections to the loopback test server) |
| 5 | Redirect-based bypass | a public URL 302s to `http://169.254.169.254/` or to `m.youtube.com` | `AllowAutoRedirect = false`, ≤ 3 hops, the full URL + blocklist + address check repeated on every hop | 02.5.2-T4 cases 11–13 |
| 6 | Non-global IPv6 ranges | `2002::/16` (6to4), `2001::/32` (Teredo), `fc00::/7`, `fe80::/10`, `fec0::/10`, `ff00::/8`, `64:ff9b::/96`, `::/96` | explicit CIDR block table + IPv4-mapped/compatible normalisation before the check | 02.5.2-T4 case 9 |
| 7 | Decompression bomb / oversized body | a 1 KB gzip expanding to 1 GB | a counting stream over the gzip/br stream that aborts at 2 MB decompressed; the compressed size is not trusted | 02.5.2-T4 case 14 |
| 8 | Proxy / credential / cookie leakage into the target | an env proxy redirecting traffic; a `Set-Cookie` replayed on a second fetch | `UseProxy = false`, `UseCookies = false`, `CookieContainer` absent, no `Authorization` header ever sent | 02.5.2-T4 cases 15–16 |
| 9 | Content-type confusion | a `text/html` response that is really a PDF/zip/executable | `Content-Type` must be `text/html`/`text/plain` **and** the first 512 bytes must sniff as text (no NUL, valid UTF-8/declared charset) | 02.5.2-T4 case 17 |
| 10 | Crawl-policy abuse | ignoring robots.txt, hammering a host, impersonating a browser | RFC 9309 subset (4xx allow, 5xx/unreachable disallow), 1 req/s per host, honest `NightTalesStudioBot` UA with a contact URL | 02.5.2-T4 cases 18–20 |
| 11 | Platform-wide fetch flood / resource exhaustion | a user pastes 1,000 URLs; many users do at once | 30 url sources per user per 24 h (429), 10 per episode (409), 4 leases platform-wide and 1 per registrable domain server-side | 02.5.1-T1 + 02.5.2-T2 tests |
| 12 | Fetcher pivots into the platform network | a compromise tries `postgres:5432` or a render node | the container has no DB credentials, no storage client, no OpenAI key, no listening port, and its egress denies app/render/metadata | 11.2.2-T2 egress test |
| 13 | Internal API abuse from outside | the `:8081` listener exposed publicly or called without a token | bound on the internal interface only, bearer token with constant-time compare, absent from the public OpenAPI, its own rate limit, network ACL from the fetcher subnet | 02.5.2-T2 tests + 11.2.2-T2 |
| 14 | Stored XSS / prompt injection from fetched text | article text containing `<script>` or "ignore your instructions" | the raw HTML is never stored and never rendered; the extracted text is stored as data, JSON-encoded into the one data object (D6), and 02.2.3-T2 owns the injection fixtures | 02.5.2-T1 (text only) + 02.2.3-T2 |
| 15 | Privacy: source URLs and text in logs/traces | Serilog request logging, exception messages containing the URL | D19 allow-list logging (ids, codes, registrable domain); the exception type carries a code, never the URL; the OTel http-client instrumentation is configured to drop `url.full`/`url.query` for the fetcher's client | 02.5.2-T1/T3 log tests |
| 16 | Retention: source text outliving the episode | `story_sources` row and object deleted, text left in storage | FK `ON DELETE CASCADE` + 13.3's deletion sweep removes `research/{userId}/{episodeId}/…`; `content_sha256` in `usage_records`-style audit rows is not stored (this feature writes none) | 13.3.1-T1 (S, ⚠️ confirm the sweep includes the `research/` prefix) |
| 17 | Token theft from the fetcher image | a compromised container reads its own env | the token grants **only** lease/heartbeat/result for tasks that are already queued; no read of episodes, users or other users' data; rotation is a redeploy of two containers (11.5.2) | design review + 02.5.2-T2 token tests |
| 18 | DoS of the internal API by a rogue task | a huge `text` body or a huge `candidates` array | request body limit 1 MiB on the internal listener, `text ≤ 300,000` chars, `candidates ≤ 50`, per-instance rate limit | 02.5.2-T2 tests |
| 19 | Parse-time resource exhaustion | a < 2 MiB page with 100k-deep nesting or > 50k elements pinning a lease slot (the parse is synchronous and outside the 10 s budget; T3's heartbeat would hold the lease) | T105 (SEC-0928-1): pre-parse guard on the decoded HTML (`ExtractMaxElements` 50,000 / `ExtractMaxDepth` 512, enforced during AngleSharp's non-recursive parse and by an iterative walk (02.5.2-T1 review F1) — a `StackOverflowException` is uncatchable) → `source_too_large`; `Extract` on a dedicated worker with a `ExtractTimeoutSeconds` = 5 watchdog → `source_no_text` (`extract_timeout`); the heartbeat self-disposes after `TotalTimeoutSeconds + ExtractTimeoutSeconds` so the lease is released | 02.5.2-T4 cases 23c/23d + T1 unit tests |

Privacy summary (D19): stored per source — normalised URL, registrable domain, final URL, title, content type, sha256, text (object storage), text length, status, error code. Deleted with the episode. Never logged: URL path/query, title, text. Never sent to OpenAI except as part of the fact-sheet call's data object (02.5.3).

## 9. Test plan

**Unit (`tests/NightTales.Domain.UnitTests/Research/`, `tests/NightTales.Application.UnitTests/Features/Research/`)**
- `SourceUrlTests`: `Parse_HttpAndHttps_Accepted` · `Parse_ExplicitPortNot80Or443_Invalid` · `Parse_UserInfo_Invalid` · `Parse_IpLiteralAnyNotation_Invalid` (dotted, decimal, octal, hex, bracketed v6, mapped v6) · `Parse_TrailingDot_NormalisedAndAccepted` · `Parse_IdnHost_ConvertedToPunycode` · `Parse_BareTldOrSingleLabel_Invalid` · `Parse_Fragment_Dropped` · `Parse_Over2048Chars_Invalid`
- `DomainBlocklistTests`: `IsBlocked_ExactDomain_True` · `IsBlocked_Subdomain_True` (`m.youtube.com`) · `IsBlocked_LookalikeDomain_False` (`notyoutube.com`, `youtube.com.evil.net`) · `IsBlocked_UnicodeConfusable_False`
- `GlobalUnicastAddressValidatorTests`: one case per CIDR in the §8 table (v4 and v6), plus `IsAllowed_Ipv4MappedLoopback_False`, `IsAllowed_PublicV6_True`
- `RobotsRulesTests` (from the T0 spec): `SelectsExactUserAgentGroup_OverWildcard` · `LongestMatchWins` · `AllowWinsTie` · `EmptyDisallow_AllowsAll` · `WildcardAndDollarMatch` · `UnknownDirectives_Ignored` · `RuleLimitExceeded_TreatedAsAllow`
- `StorySourceTests`: create/retry/state transitions, `CreateNote_WithUrl_Fails`, `MarkFetched_WithoutSha256_Fails`
- `AddSourceHandlerTests`: `Add_BlockedDomain_ReturnsSourceDomainNotAllowed` · `Add_EleventhSource_ReturnsSourceLimitReached` · `Add_ThirtyFirstUrlSourceIn24h_Returns429` · `Add_DuplicateNormalisedUrl_Returns409` · `Add_NoteSource_DoesNotCreateFetchTask` · `Add_WhileStoryApproved_ReturnsEpisodeInvalidState` · `Add_CreatesSourceAndTaskInOneTransaction`

**Integration (`tests/NightTales.Api.IntegrationTests/Features/Research/`, real PostgreSQL 17 via `PostgresFixture` + in-process Kestrel)**
- `AddSource_ThenFetch_EndToEnd`: add → lease → post a result → `GET sources` shows `fetched` with `contentSha256`/`textChars` and the object exists under `research/{userId}/{episodeId}/{sourceId}/{sha256}.txt`
- `Lease_RespectsPlatformCapOfFour` · `Lease_SecondTaskSameRegistrableDomain_NotLeased` · `Lease_EmptyQueue_ReturnsEmptyArray`
- `Heartbeat_ExtendsLease_AndOnlyOwnWorker` · `Heartbeat_AfterExpiry_Returns409FetchLeaseLost`
- `Sweeper_RequeuesExpiredLease` · `Sweeper_SecondExpiry_MarksFailedSourceUnreachable` · `Sweeper_DoesNotTouchActiveLeases`
- `Result_TextSha256Mismatch_Returns400SourceNoText_AndTaskStaysLeased` · `Result_OversizeText_Returns400SourceTooLarge` · `Result_BlockedFinalUrl_Returns400SourceDomainNotAllowed`
- `InternalApi_WithoutToken_Returns401` · `InternalApi_Route_NotPresentOnPublicListener` (`GET localhost:8080/internal/fetcher/v1/tasks/lease` → 404) · `InternalApi_TokenComparison_IsConstantTime` (design assertion via the handler test double)
- `FindSourcesTask_Lease_ReturnsFinderRequestShape` · `FindSourcesResult_Candidates_StoredAsCandidates` (02.7 not shipped → the task kind is exercised against a stub runner)

**The SSRF/abuse suite is 02.5.2-T4** (its own task, S) and covers all 37 cases (34 + 23b + 23c + 23d, 02 T104/T105) listed in the task file, including the loopback test server reached **only** through the test-only `IAddressValidator` (D20) and the `FakeDnsResolver` rebinding case. It is the acceptance evidence for the `02-M1-research` gate ("the SSRF suite is green", README §7).

**Architecture test**: `ProductionComposition_DoesNotRegisterLoopbackAllowingAddressValidator` — asserts `IAddressValidator` resolves to `GlobalUnicastAddressValidator` in the Api/Worker/Fetcher DI graphs, so the test-only validator cannot leak into production (D20).

**Manual**: fetch 5 real Vietnamese news pages (vnexpress.net, tuoitre.vn, thanhnien.vn, dantri.com.vn, nhandan.vn) and 5 English (bbc.com, reuters.com, apnews.com, nytimes.com, wikipedia.org) and record extraction quality (title present, main text ≥ 200 chars, no nav/footer) in the T0 report; re-run after any extractor change.

## 10. Tasks and re-estimate

| Task | Title | Size | Depends on | Blocks |
|---|---|---|---|---|
| 02.5.1-T1 | `story_sources` + `note_text` + `fetch_tasks` + add/remove/list/retry + caps | M (1.0) | 09 episodes (09.5.1) | 02.5.2-T2, 02.5.3-T1 |
| 02.5.2-T0 | Spike: extractor (SmartReader vs trafilatura) + robots parser (in-house RFC 9309 subset spec) + 20-page corpus | S (0.5) | — | 02.5.2-T1 |
| 02.5.2-T1 | `SafeHttpSourceFetcher` (D11 in full) + `IArticleExtractor` | M (1.0) | 02.5.2-T0 | 02.5.2-T2, T3, T4 |
| 02.5.2-T2 | Internal lease API on `:8081` + `FetchLeaseSweeperJob` + text storage | M (1.0) | 02.5.1-T1, 02.5.2-T1, 09.7.2-T1 (09 W2 CR-6) | 02.5.2-T3, 02.7.2-T2 |
| 02.5.2-T3 | `NightTales.Fetcher` host + container + 11.2.2-T2 network contract | M (1.0) | 02.5.2-T1, T2 | 02.5.2-T4, 11.2.2-T2 |
| 02.5.2-T4 | SSRF/abuse integration suite (all 02 §7 + F14 cases) | S (0.5) | 02.5.2-T3 | `02-M1-research` gate |

**Total 5.0 d**, matching README §9 ("02.5.1/02.5.2 sources + fetcher: 2.5 → 5.0, pull-model fetcher host + lease API + SSRF suite (F02, F14, F15)"). Sequential order for one developer: T0 → T1 → 02.5.1-T1 (independent of T0/T1, may run first or in parallel) → T2 → T3 → T4. T4 can start its harness (the test-only validator, the fake DNS resolver, the loopback server) as soon as T1 lands; the task is scheduled after T3 so the last acceptance case (the fetcher host's own behaviour under `409 fetch_lease_lost`) is testable.

**Open items for the TL**

> **Status, 2026-09-23.** TL rulings T38–T43 (CR-19, CR-20) resolved items 1, 2, 3, 4, 6, 7, 9, 10, 11 and 12 — marked ✅ below. Items 5 (informational) and 8 (calibrated by 02.5.2-T0) remain open. Numbering is preserved so the task files' `[OPEN] n` references stay valid. Separately resolved by T43: the `Retry-After` header for the 429 `source_fetch_limit_reached` comes from 09's `ProblemDetails` mapper (CR-20), not from a local header in 02.5.1-T1.

1. ✅ **Resolved (T38).** D10's `fetch_tasks` column list gains four columns, now in §3.2: `url text NULL` and `registrable_domain text NULL` (the lease response returns `url`; the 1-lease-per-domain cap needs a domain key that also exists for `find_sources`), plus `generation_run_id uuid NULL` (the `find_sources` run whose payload is re-derived at lease time, so no brief text is duplicated into a table the fetcher reads) and `leased_at timestamptz NULL` (so `fetch_duration_seconds` measures fetch time, not queue time). `episode_id` becomes **NOT NULL**: a `find_sources` task is episode-level too.
2. ✅ **Resolved (T39).** Every result body carries `status`: `"done"` (with the text fields for `fetch_source`, or with `candidates` for `find_sources`) or `"failed"` (with a D8 job code). §5.2 updated; 02.7.2-T2 must match.
3. ✅ **Resolved (T40).** `src/NightTales.Fetcher.Core/` is accepted (Domain only), referenced by both `NightTales.Fetcher` and `NightTales.Infrastructure`. 00 §5.2 needs the layout row added; `plan.md` §7.2 and 02.5.2-T3 carry the rationale and the dependency-assertion test.
4. ✅ **Resolved (T41).** A `user_note` row is created `Fetched` with `content_sha256 = sha256(UTF8(NFC(note_text)))`, `text_chars = noteText.Length`, `extracted_text_key = null`. 02.5.4-T1 counts only `kind = 'url'` toward FR-02-4's two cited sources.
5. **F13's "≥ 2 distinct `content_sha256`" needs `content_sha256` on `story_sources`** — CR-4 already adds it; this note exists so 02.5.4-T1 does not re-derive the hash from the stored text. (Informational, no action.)
6. ✅ **Resolved (T43).** The fetcher gets **no** storage egress: the Api writes the extracted-text object, never the fetcher. 11.2.2-T2's "internal write API wiring" means `api:8081` only.
7. ✅ **Resolved (T43).** Robots `Crawl-delay` **is** honoured: the per-host interval is `max(1 s, crawlDelay)`, capped at **10 s**; a `crawl-delay` above 10 s → `source_blocked_by_robots` (we will not wait longer than our own page budget). `Sitemap` stays ignored. 02.5.2-T0's spec and 02.5.2-T1's limiter implement `max(1 s, crawlDelay)`.
8. ~~**`source_no_text` threshold (~200 chars) is a guess**~~ **Closed 2026-09-28 (02 T104):** `Fetcher:MinTextChars` = **1,275** from the 02.5.2-T0 corpus (60 % of the 2,125-char shortest clean article under SmartReader 0.11.1; failure shapes 0 / 252 / 1,226 chars fall below). 02.5.2-T4 pins the boundary.
9. ✅ **Resolved (T38).** `fetch_tasks.episode_id` is NOT NULL, so every task (including `find_sources`) is deleted with its episode by the FK cascade — the "task with no episode" retention gap is gone. Still to confirm with 13.3: that 13.3.1-T1's deletion sweep covers the `research/` storage prefix (the Api writes those objects).
10. ✅ **Resolved (T42).** The lease statement takes a `pg_advisory_xact_lock` on a constant for the duration of the claim, so the platform cap and the per-domain cap are exact rather than best-effort; the unlocked-read disclosure for both `leased_now` and `domain_busy` is documented in 02.5.2-T2 as the reason the lock exists.
11. ✅ **Resolved (T43).** `fetch_lease_lost` and `fetch_task_not_found` stay as internal-only codes on the `:8081` routes; they never appear in D8 or in `story_sources.error_code`.
12. ✅ **Resolved (T43).** The fetcher container has **no** listening port: liveness is "can lease", and the host exits non-zero after 5 consecutive internal-API failures so the supervisor restarts it. No `/healthz` is added.

## 11. Rollout

- **Flags.** No feature flag of its own: 02.5.1/02.5.2 are `Must` and always on once deployed. `source_finder` (02.7) gates only the `find_sources` task kind; before 02.7 lands the lease endpoint can never return such a task because nothing inserts one.
- **Order.** Migration → 02.5.1-T1 (API + caps, no fetch yet: sources stay `pending` forever, acceptable only inside the wave) → 02.5.2-T1/T2/T3 → 11.2.2-T2 (container + subnet + egress test) → T4 green → `02-M1-research` gate. The fetcher container is deployed **after** the internal API exists; until then no lease request is served and no task is lost (tasks wait `queued`).
- **Config defaults.** Every cap ships at the D11 value. Lowering `Fetcher:MaxLeasesPlatformWide` below 4 is allowed; raising it above 4 needs the TL's sign-off because FR-02-8's number is a decision, not a tuning knob. `Fetcher:CrawlDelayMaxSeconds = 10` ships with the robots `Crawl-delay` rule (T43).
- **Token rotation (11.5.2).** `FETCHER_API_TOKEN` is a runtime secret read by both the Api (verify) and the fetcher (send). Rotation = write the new value to the secret manager, restart the Api (accepts only the new value), then restart the fetcher. A window where the fetcher still holds the old token fails every lease with 401, which the fetcher treats as fatal after 5 consecutive 401s (log + exit 1 → the supervisor restarts it). No dual-token grace period in P [DECISION: a second accepted token is a second credential to leak for no benefit].
- **Rollback.** The migration is additive (two new tables + new nullable columns on `story_sources`), so a rollback of the code leaves the tables unused. Rolling back T3 alone (stop the container) is safe: leases expire in ≤ 90 s and the sweeper requeues. Rolling back T2 (the internal API) with the container still running produces 404s on the lease route → the fetcher logs and retries; no data loss.
- **Gate evidence for `02-M1-research`.** T4's suite green (all 37 cases), the 11.2.2-T2 egress test green, and the manual 10-page extraction record from T0 kept in the spike report. No OpenAI spend, so no budget or metering change is needed for this feature.
