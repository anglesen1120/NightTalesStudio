# 09.8.1 — Real-time progress over SignalR

> Parent: ../README.md (Part B)

## 1. Scope

**Story.** 09.8.1 *"As a creator I want live progress so that I do not refresh the page."* Must · **M 2 tasks** (09.8.1-T1 M, 09.8.1-T2 S — README B8).

**Refines.** `../../../09-backend-api.md` §4 epic 09.8 (story **09.8.1**, L213–219); `../../../00-overview.md` §8 (four SignalR events).
**Contract.** `../README.md` — **D5**, **D7**, **D10**, **D11**, **D12**, **D13**, **D14**, **BD1**, **BD3**, **BD11**, **BD14**, **BD16**, **WF10**; 02 README plan 02.8.2 L227–228 (`AutopilotRunUpdated` ask).
**Template.** `../../../00-overview.md` §10.1. Legend: ✅ · ⚠️ · [DECISION] · [OPEN] · [INFERENCE].
**Status.** detailed plan, 2026-09-24. Nothing implemented yet.

**In scope.**

- Hub `/hubs/episodes` — **WebSockets only** (`skipNegotiation`), groups `user:{id}` only, no sticky sessions required (BD11 ✅, 09 L217 ✅); the load balancer must carry WebSockets (11 README L253, 11.2.2-T3) or sticky sessions are configured instead (09 L217).
- `IRealtimeNotifier.NotifyAsync(Guid userId, RealtimeEvent e, ct)` + `PgNotifyRealtimeNotifier` → `SELECT pg_notify('realtime', <json shorter than 8 KB: userId, event, ids>)` **inside the caller's transaction**, delivered at commit (BD11 ✅, D5 ✅).
- `RealtimeListenerService` — **one** `LISTEN` connection per Api instance, reconnect with backoff 1→30 s, fan-out to that instance's local sockets; on reconnect the client refetches (BD11 ✅, 09 L216 ✅).
- Events: `EpisodeUpdated`, `RenderProgress`, `PublicationUpdated`, `CreditBalanceChanged` (S). `RenderProgress` is emitted by `PollRenderJob` only when stage/percent changed (BD7 ✅).
- The `security` channel from 09 L218 (`UserSecurityChanged` → abort that user's live connections) and the connection cap backstop `Hubs:MaxConnectionMinutes` = the role's access-token lifetime (owner 5, user 15) with **no stamp cache in P** ([RULED — README §B10 WT37]), because this task owns `RealtimeListenerService` and the connection registry.
- `POST /hubs/ticket` + `hub_tickets` (09.8.1-T2): hashed, single-use, 60 s, claimed by one `UPDATE … WHERE used_at IS NULL`, 401 `hub_ticket_invalid`, `HubTicketCleanupJob` hourly (BD11 ✅), rate policy `hub` (BD14 ✅).

**Out of scope.**

- Client work (10.2.4 reconnect/refetch, `@microsoft/signalr`) and any UI.
- `UserSecurityChanged` production (09.3.2-T1) and the ≤ 30 s user-status cache reader (owner: 09.3.2-T1 / 09.10.4-T1) — this folder consumes them.
- Producing the events: 02, 06 and 09.6.3 call the notifier; this folder ships the port and the transport.
- `AutopilotRunUpdated` — an open ask from 02.8.2 (L227–228), **not** in 00 §8's four events; recorded here as [OPEN] (see §5, §10) and **not** implemented.

## 2. Confirmed facts

| Id | Fact | Source |
|---|---|---|
| R1 | Hub `/hubs/episodes`, WebSockets only (`skipNegotiation`), groups `user:{id}`; `IRealtimeNotifier.NotifyAsync(Guid userId, RealtimeEvent e, ct)` → `NOTIFY realtime` inside the caller's transaction; `RealtimeListenerService`, one `LISTEN` per Api instance, backoff **1→30 s**, clients refetch on reconnect | BD11 ✅ |
| R2 | Artefact ownership: hub, `IRealtimeNotifier`, `PgNotifyRealtimeNotifier`, `RealtimeListenerService` = 09.8.1-T1 (consumers 02, 06, 09.6.3); `hub_tickets`, `POST /hubs/ticket` = 09.8.1-T2 (consumer 10.2.4) | BD1 ✅ |
| R3 | `hub_tickets(ticket_hash, user_id, created_at, expires_at, used_at)`; ticket = 32 random bytes, 60 s; claim `UPDATE … SET used_at = now() WHERE ticket_hash = @h AND used_at IS NULL AND expires_at > now() RETURNING user_id`, else 401 `hub_ticket_invalid`; `HubTicketCleanupJob` hourly | BD11 ✅ |
| R4 | `hub_tickets` is created by **09.8.1-T2**'s own migration (owner in the D9 owner table) | D9 (CR-5) ✅ |
| R5 | Rate policies `auth`, `paid`, **`hub`** are owned by 09.3.5-T1; `hub` = 10 tickets/min per user; rejection 429 `rate_limited` + `Retry-After` set in `OnRejected` | BD1, BD14 ✅ |
| R6 | Outbox handlers run per message in their **own DI scope** under `ISystemContext.For(user_id)`, are idempotent, and a handler that changes data sends a command (pipeline + UnitOfWork apply) | D11 ✅ |
| R7 | The **Api never schedules jobs**; side effects go through the outbox and its handlers run in the Worker | D12 ✅ |
| R8 | `[JobPolicy(Queue = …, Retries = n)]`, queues `default`, `render`, `publish`, `email`; recurring registrations via `IRecurringJobRegistration` at Worker start; ids-only job parameters (arch test) | D12 ✅ |
| R9 | Security rules for wave 2: no secret/token in logs; all tokens random ≥ 256 bit and **stored hashed**; constant-time comparisons; ids-only payloads; tenant filter everywhere; 404 for other users' resources; LISTEN payload ≤ 8 KB | BD16 ✅ |
| R10 | Performance rules: render polling batched (≤ 50/tick, parallelism 4); `PollRenderJob` notifies `RenderProgress` **only when stage/percent changed**; health endpoints read caches only | BD16, BD7 ✅ |
| R11 | Ticket: authenticated with the access token, random, valid 60 s, usable once, **stored hashed with user id and security stamp**; client connects to `/hubs/episodes?ticket=…`; missing/expired/reused/**stamp-mismatched** ticket → 401 `hub_ticket_invalid`; **the JWT never travels in a query string** | 09 L214 ✅ |
| R12 | `UserSecurityChanged` → `NOTIFY` on a **separate channel `security`** (payload = user id only); every Api instance aborts that user's live connections; as a backstop each instance closes a connection when the **role's access-token lifetime** (`Hubs:MaxConnectionMinutes`: owner 5, user 15) elapses, with **no stamp cache in P** ([RULED — README §B10 WT37]) | 09 L218; README §B10 WT37 |
| R13 | With two Api instances behind the load balancer (11.2.2), WebSockets-only means no sticky sessions; otherwise sticky sessions | 09 L217; 11 README L253 ✅ |
| R14 | 00 §8 lists **four** SignalR events only; 02.8.2 (L227–228) requests a fifth, `AutopilotRunUpdated { seriesId, runId, slotAt, state, outcome, pauseReason }`, published by the outbox handler in the Worker and forwarded through `IRealtimeNotifier` to group `user:{id}`, and marks the run log as polling on tab focus until then | 00 §8; 02.8.2 L227–228 ✅ |
| R15 | Owner/admin access tokens live **5 min**, user tokens 15 min; `sid` = refresh family; `UserSecurityChanged` is produced by 09.3.2-T1 | BD3 ✅ |

## 3. Domain and data

**No Domain change.** Real-time is transport: this folder adds **no aggregate, no domain event, no state machine**, and it never mutates an episode. Events are *facts already committed* by their owners, re-broadcast as ids.

**One new table**, created by 09.8.1-T2 with its own expand-only migration `<yyyyMMddHHmm>_09_8_1_HubTickets` (D9 naming ✅):

```sql
CREATE TABLE hub_tickets (
    ticket_hash text        PRIMARY KEY,          -- SHA-256 hex (64 chars) of the 32-byte ticket; never the ticket
    user_id     uuid        NOT NULL REFERENCES users(id) ON DELETE CASCADE,
    created_at  timestamptz NOT NULL,
    expires_at  timestamptz NOT NULL,             -- created_at + 60 s (BD11)
    used_at     timestamptz NULL                  -- non-null = consumed; single use
);
CREATE INDEX ix_hub_tickets_expires_at ON hub_tickets (expires_at);   -- HubTicketCleanupJob
```

Single-use claim (BD11 verbatim, R3):

```sql
UPDATE hub_tickets SET used_at = now()
WHERE ticket_hash = @h AND used_at IS NULL AND expires_at > now()
RETURNING user_id;
```

No `SELECT … FOR UPDATE`, no advisory lock, no Redis: the row update is the claim, so the first of two concurrent connects wins and the second sees 0 rows. `ticket_hash` is compared by **hash lookup, not by comparing the secret in code**, so no constant-time comparison is needed for the ticket itself (the hash is the primary key; BD16's constant-time rule applies where a comparison happens in code — here it does not).

[RULED — README §B10 WT37] the open question above is settled: **no stamp cache in P** — the connection cap is `Hubs:MaxConnectionMinutes` per **role's access-token lifetime** (owner 5, user 15), every family revocation still sends `NOTIFY security` (user id) and the hub aborts that user's connections, and no `security_stamp`/`token_expires_at` column is added to `hub_tickets` (a stamp check is deferred). The BD11 column list stands as written.

Payload on the wire (**ids only** in `ids`, shorter than 8 KB — BD11/BD16 ✅; `data` is the one ruled exception, WT54):

```json
{ "userId": "0192…", "event": "render_progress",
  "ids": { "episodeId": "0192…", "renderJobId": "0192…" },
  "data": { "stage": "voice", "percent": 12 } }
```

`data` is present only for `RenderProgress` and carries **only** `stage` (enum) and `percent` (0–100) ([RULED — README §B10 WT54]); every other event sends `data: null`.

## 4. Use cases

| # | Actor → goal | Flow | Result |
|---|---|---|---|
| UC1 | 10.2.4 → open a live channel | `POST /hubs/ticket` (bearer, policy `hub`) → `{ ticket, expiresAt }`; connect `/hubs/episodes?ticket=…` (WebSockets) → claim → join `user:{id}` | connection in that user's group; 401 `hub_ticket_invalid` on a bad/expired/reused ticket |
| UC2 | 02/06/09.6.3 → tell the UI something changed | inside the command/outbox scope: `IRealtimeNotifier.NotifyAsync(userId, e, ct)` → `pg_notify` in the **caller's** transaction | delivered at commit; rolled back with the transaction (never a phantom event) |
| UC3 | 09.6.3 `PollRenderJob` → progress bar moves | render status changed → one `RenderProgress` per change (stage/percent), grouped per poll tick | client refetches the render block; `data` carries `stage` + `percent` **only** ([RULED — README §B10 WT54]) |
| UC4 | Api instance without the socket → must still deliver | every instance `LISTEN realtime` and fans out to its **local** group members | exactly the instance holding the socket sends; no cross-instance routing needed |
| UC5 | security → kill live access | every family revocation → `NOTIFY security` (user id only) → each Api aborts that user's connections; plus the backstop that closes a connection after `Hubs:MaxConnectionMinutes` = the role's access-token lifetime (owner 5, user 15); no stamp cache in P ([RULED — README §B10 WT37]) | immediate on the message, ≤ the role's token lifetime otherwise |
| UC6 | any user → avoid ticket spam | policy `hub` = 10 tickets/min per user → `429 rate_limited` + `Retry-After` (BD14/D7) | bounded ticket issuance |
| UC7 | listener/ticket table hygiene | `HubTicketCleanupJob` hourly deletes `expires_at < now − 1 d` | bounded table |
| UC8 | 02.8.2 run log → needs `AutopilotRunUpdated` | [OPEN] not implemented; 00 §8 lists four events only (R14) | run log polls on tab focus until the TL decides |

## 5. API and events

| Method | Route | Auth | Success | Errors |
|---|---|---|---|---|
| POST | `/api/v1/hubs/ticket` | bearer access token; rate policy `hub` | `200 { "ticket": "<43-char base64url>", "expiresAt": "2026-09-24T10:00:00Z" }` | `401` unauthenticated, `429 rate_limited` (+`Retry-After`), `503 service_unavailable` |
| GET (WS) | `/hubs/episodes?ticket=…` | ticket (scheme `HubTicket`), WebSockets only | `101` upgrade + group `user:{id}` | `401 hub_ticket_invalid` (missing/expired/used/unknown), `400` non-WebSocket transport |

Contracts:

```csharp
namespace NightTales.Application.Abstractions.Realtime;

public sealed record RealtimeEvent(RealtimeEventName Name, RealtimeIds Ids, RealtimeData? Data = null);   // [RULED — README §B10 WT54]
public enum RealtimeEventName { EpisodeUpdated, RenderProgress, PublicationUpdated, CreditBalanceChanged }  // [INFERENCE: kind]
public sealed record RealtimeIds(Guid? SeriesId = null, Guid? EpisodeId = null, Guid? RenderJobId = null,
                                 Guid? PublicationId = null, Guid? StoryVersionId = null);                  // [INFERENCE]
public sealed record RealtimeData(string? Stage, short? Percent)                                          // [RULED — README §B10 WT54]
{                                                                                                        // for RenderProgress only:
    public static RealtimeData RenderProgress(RenderStage stage, short percent) => new(stage.ToString(), percent);
}                                                                                                        // `stage` enum + `percent` 0–100

public interface IRealtimeNotifier
{
    Task NotifyAsync(Guid userId, RealtimeEvent e, CancellationToken ct);              // BD11 signature ✅
}
```

[RULED — README §B10 WT54] the `RealtimeEvent(Name, Ids, Data?)` shape is frozen: `RealtimeData` is allowed **only** for `RenderProgress` and may carry **only** `stage` (the `RenderStage` enum) and `percent` (0–100); all other events leave `Data = null` and the NOTIFY body must stay shorter than 8 KB. `RealtimeIds`/`RealtimeEventName` remain the wave-2 [INFERENCE] shapes to confirm at implementation.

`PgNotifyRealtimeNotifier` (Infrastructure `Realtime/PgNotifyRealtimeNotifier.cs`):

```csharp
public sealed class PgNotifyRealtimeNotifier(AppDbContext db) : IRealtimeNotifier
{
    public const int MaxPayloadBytes = 8_000;                 // BD11/BD16, PostgreSQL's own exclusive limit
    public async Task NotifyAsync(Guid userId, RealtimeEvent e, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(new { userId, @event = e.Name.ToWireName(), ids = e.Ids, data = e.Data }, Json);
        if (Encoding.UTF8.GetByteCount(json) >= MaxPayloadBytes) { /* drop + metric + error log, never truncate */ }
        await db.Database.ExecuteSqlRawAsync("SELECT pg_notify('realtime', {0})", [json], ct);
    }
}
```

[DECISION] `ExecuteSqlRawAsync` on the caller's `AppDbContext` joins the ambient transaction opened by `UnitOfWorkBehavior` (D5), so PostgreSQL delivers the notification **at commit** and discards it on rollback; with no ambient transaction (an outbox handler, D11) it is delivered immediately after the statement. The notifier **never** opens its own transaction and never writes a row. Cost: one round trip per event, only on real changes (BD7 gates `RenderProgress` on a stage/percent change).

Transport wiring (Api):

```csharp
// Api/Realtime/EpisodeHub.cs
[Authorize]
public sealed class EpisodeHub : Hub
{
    public override async Task OnConnectedAsync()
    {
        var userId = Context.UserIdentifier ?? throw new HubException("no subject");
        await Groups.AddToGroupAsync(Context.ConnectionId, $"user:{userId}");   // never from client input
        await base.OnConnectedAsync();
    }
}
```

- Group name comes from `Context.UserIdentifier` (set by the `HubTicket` principal's `sub`), **never** from a client parameter — a client cannot choose a group.
- `AddSignalR()` on the Api only; `MapHub<EpisodeHub>("/hubs/episodes")` with `HttpTransportType.WebSockets` (`skipNegotiation` on the client, 10.2.4) and no sticky session.
- Server-side SignalR ships in the `Microsoft.AspNetCore.App` shared framework (no NuGet package) ⚠️ [INFERENCE: confirm no extra package at implementation; the JS client is plan 10's].
- `RealtimeListenerService : BackgroundService` (Api) — own long-lived `Npgsql` connection (never an EF `DbContext`), `LISTEN realtime; LISTEN security;`, `WaitAsync` loop, reconnect backoff **1, 2, 4, 8, 16, 30 s** (BD11 ✅), on message parse → `IHubContext<EpisodeHub>.Clients.Group($"user:{userId}").SendAsync(eventName, payload, ct)`; channel `security` → `IRealtimeConnectionRegistry.AbortUser(userId)`. Two channels on one connection (PostgreSQL allows one connection to listen on many), so "one LISTEN connection per Api instance" (BD11 ✅) holds.
- Connection registry (in-memory, per instance): `userId → { (connectionId, HubCallerContext, claimedAt, maxConnectionMinutes) }` where `maxConnectionMinutes` comes from the claimant's **role** (owner/admin 5, user 15 — `Hubs:MaxConnectionMinutes`, WT37), so the `security` message and the backstop can `Abort()` exactly the right connections; there is **no stamp cache in P** ([RULED — README §B10 WT37]).

Event method names sent to clients: `EpisodeUpdated`, `RenderProgress`, `PublicationUpdated`, `CreditBalanceChanged` — exactly the 00 §8/09 L215 names ✅ (SignalR method names are strings; the client subscribes to these) [INFERENCE: keep them PascalCase; JSON *bodies* stay camelCase per D14].

## 6. UI

Consumed by 10.2.4 and the episode page; no UI in this folder.

- Client: fetch a ticket → `new HubConnectionBuilder().withUrl('/hubs/episodes?ticket=…')` (`skipNegotiation = true`, transport WebSockets only) → subscribe to the four methods → on `EpisodeUpdated`/`RenderProgress`/`PublicationUpdated` refetch `GET /episodes/{id}` (09.5.3-T1) rather than patching state from the payload, because payloads carry **ids only** (BD11) [DECISION].
- Reconnect: on `onreconnected`/`onclose` the client fetches a **new** ticket (the old one is single-use, 60 s) and refetches state (BD11 "on reconnect clients refetch" ✅).
- `CreditBalanceChanged` (S) is subscribed only when the balance UI exists (plan 12); the server sends it whenever that event is raised — no flag is added in P.

## 7. Jobs and integrations

- `HubTicketCleanupJob` (Application `Features/Realtime/Jobs/HubTicketCleanupJob.cs`, `sealed`, `IBackgroundJob`, `[JobPolicy(Queue = JobQueue.Default, Retries = 2)]`) — deletes `hub_tickets` where `expires_at < now − 1 d` in batches; registered **hourly** as a recurring job at Worker start through `IRecurringJobRegistration` (D12, BD11 ✅). The **Api never schedules it** (R7).
- Producer side (other features, not this folder): 02/06/09.6.3 call `IRealtimeNotifier` from a command handler or an outbox handler (R6). `PollRenderJob` calls it only when stage/percent changed (BD7 ✅).
- Monitor side does not use this hub; `/health/*` reads caches only (BD12/R10).
- Tuning keys (all with defaults):

| Key | Default | Purpose |
|---|---|---|
| `Realtime:TicketTtlSeconds` | `60` | Ticket lifetime (BD11) |
| `Realtime:TicketCleanupRetentionHours` | `24` | `HubTicketCleanupJob` retention |
| `Realtime:ListenerBackoffSeconds` | `1,2,4,8,16,30` | Listener reconnect schedule (BD11) |
| `Hubs:MaxConnectionMinutes` | owner/admin `5`, user `15` | Absolute connection cap = the **role's** access-token lifetime ([RULED — README §B10 WT37]; replaces the former single `Realtime:MaxConnectionMinutes`) |
| `Realtime:RevalidateSeconds` | `60` | backstop sweep interval; it closes only on the `Hubs:MaxConnectionMinutes` cap in P — **no stamp cache** (WT37) |
| `Realtime:MaxPayloadBytes` | `8000` | Exclusive cap (PostgreSQL refuses a payload this long); a body this long or longer drops the event and increments a metric |
| `Realtime:DeliveryQueueCapacity` | `1024` | Bounded queue between the `LISTEN` loop and the one group sender; a full queue drops its oldest event and increments a metric, so a socket under back-pressure never holds the loop, the `security` abort or the backstop |
| `Realtime:DeliveryTimeoutSeconds` | `10` | Per-send bound of the delivery consumer; a send that exceeds it is abandoned and counted, so one slow socket cannot hold the events behind it |

Metrics (OTel, BD12/09.9.3-T1 names it): `realtime_notify_total{event}`, `realtime_notify_dropped_total`, `realtime_listener_reconnects_total`, `realtime_listener_messages_total{channel}`, `realtime_listener_deliveries_dropped_total`, `realtime_listener_deliveries_failed_total`, `realtime_connections_active` (gauge) [INFERENCE: exact names].

## 8. Security and privacy

- **Ticket is a bearer secret.** 32 random bytes (`RandomNumberGenerator`), base64url; stored **only** as SHA-256 hex (BD16 ✅); TTL 60 s; single use via one atomic `UPDATE`; never logged (not the ticket, not the hash, not the URL). The access log redaction processor (09.9.3-T2 — which B8.1 makes depend on this task ✅) must strip the `ticket` query parameter; until then the Api must not log `Request.QueryString` for the hub path (checked in review).
- **JWT never in a query string** (R11 ✅): the ticket is the only URL credential, and it dies at connect.
- **Group membership is server-derived** from `Context.UserIdentifier`; a client cannot subscribe to another user's group, and the listener re-checks that the message's `userId` matches the group before sending (defence in depth against a malformed payload).
- **Ids only, shorter than 8 KB** (BD11/BD16 ✅): no story text, no titles, no e-mail, no IP, no token, no storage key. The **one** ruled exception is `RenderProgress`, whose `data` carries `stage` + `percent` (0–100) and nothing else ([RULED — README §B10 WT54]); every other event sends `data: null`, and an over-sized payload is dropped with a metric instead of being truncated (a truncated JSON is worse than a lost event; the client refetches anyway).
- **Tenant/authorization:** the notifier takes `userId` from the calling scope's `ICurrentUser`/`ISystemContext` (D10) — producers must pass their own user id, never a client-supplied one; the hub only ever serves that user's group.
- **Revocation:** every family revocation sends `NOTIFY security` (user id only) and the hub aborts that user's live connections; the backstop closes a connection once `Hubs:MaxConnectionMinutes` = the role's access-token lifetime (owner 5, user 15) has elapsed — **no stamp cache is read in P** ([RULED — README §B10 WT37]); the ticket is never re-usable, so a stolen one is useless after use/60 s.
- **Transport:** TLS only (11.2.2-T3), `Origin` validation on the WebSocket handshake against `App:PublicOrigin` (same rule as cookie POSTs, BD16) so a foreign page cannot open a socket with a victim's ticket; browser clients send no cookies to the hub (the ticket is in the URL, the `__Secure-nts_rt` cookie is scoped to `/api/v1/auth`, BD3 ✅).
- **Bounded work:** one `LISTEN` connection per instance (BD11 ✅); `HubTicketCleanupJob` deletes in batches; the registry drops entries on disconnect (no leak); notification cost is one SQL round trip per changed event (BD7 gates progress).
- **Secrets:** the listener uses the same DB connection string from the secret manager; nothing new is introduced.

## 9. Test plan

Harness: D13 (`ApiFactory`, `TestAuthHandler`, `PostgresFixture`, `WorkerHost`, `InlineJobScheduler`, `FakeClock`; hand-written fakes, **no mocking library**). The hub tests use a real `HubConnection` over WebSockets against `ApiFactory`'s `TestServer` (D13's `ApiFactory` is a `WebApplicationFactory<Program>`; a real socket needs `TestServer`'s in-memory transport, so the WebSockets-only rule is asserted on the mapping/options and one end-to-end test runs the host on a random port) [INFERENCE: exact test transport].

`tests/NightTales.Api.IntegrationTests/Realtime/HubTicketTests.cs` (09.8.1-T2)
- `Ticket_Is_Returned_Once_And_Stored_Hashed_Only`
- `Ticket_Is_Single_Use` (two sequential claims against two `ApiFactory` instances sharing one `PostgresFixture`)
- `Two_Concurrent_Claims_One_Wins` (two tasks, one `RETURNING` row total)
- `Expired_Ticket_Is_Rejected` (`FakeClock` + 61 s)
- `Unknown_Or_Tampered_Ticket_Is_Rejected`
- `Missing_Ticket_Is_Rejected_With_401_HubTicketInvalid`
- `Ticket_Requires_Access_Token`
- `Eleventh_Ticket_In_A_Minute_Is_429_RateLimited_With_RetryAfter`
- `CleanupJob_Deletes_Only_Rows_Older_Than_Retention` (`WorkerHost` + `InlineJobScheduler` + `FakeClock`)
- `No_Log_Line_Contains_The_Ticket` (asserts against a capturing Serilog sink in TestKit)

`tests/NightTales.Api.IntegrationTests/Realtime/RealtimeHubTests.cs` (09.8.1-T1)
- `Connect_With_Valid_Ticket_Joins_Only_Its_Own_User_Group`
- `Client_Cannot_Choose_Its_Own_Group`
- `Non_WebSocket_Transport_Is_Rejected`
- `EpisodeUpdated_Is_Delivered_To_The_Owner`
- `Event_Is_Not_Delivered_To_Another_User`
- `Disconnect_Removes_The_Group_Membership_And_Registry_Entry`

`tests/NightTales.Application.UnitTests/Realtime/PgNotifyPayloadTests.cs`
- `Payload_Contains_UserId_Event_Ids_And_Ruled_Data_Only` (whitelist assertion over the serialized JSON; `data` appears only for `RenderProgress` and holds `stage` + `percent` — WT54)
- `Payload_Under_Limit_Is_Sent` (hand-written recording connection fake in TestKit)
- `Payload_Over_8000_Bytes_Is_Dropped_And_Counted` (ids-only payloads cannot reach it in practice — the test feeds a synthetic event and asserts the drop + metric, never truncation)

`tests/NightTales.Api.IntegrationTests/Realtime/RealtimeListenerTests.cs`
- `Listener_Forwards_Notify_To_Local_Connections_Only`
- `Listener_Reconnects_With_Backoff_After_A_Dropped_Connection` (a hand-written `INpgsqlListenerConnectionFactory` fake counts attempts; asserts 1, 2, 4 s, no sleeps)
- `Security_Channel_Aborts_That_Users_Connections_Only`
- `Backstop_Closes_AnOwnerConnection_AfterFiveMinutes_AndAUserConnection_AfterFifteen` (`FakeClock`, per-role `Hubs:MaxConnectionMinutes` — WT37)
- `Backstop_Reads_NoStampCache` (asserts no user-status cache dependency is resolved in P — WT37)
- `Notify_Inside_A_Rolled_Back_Transaction_Is_Not_Delivered` (command returns an error → no `pg_notify` reaches the listener)
- `Notify_Inside_A_Committed_Transaction_Is_Delivered_Once`

## 10. Tasks and re-estimate

| Task | Title | Area | Size | Depends on | Blocks |
|---|---|---|---|---|---|
| 09.8.1-T1 | Hub `/hubs/episodes`, `IRealtimeNotifier` (`pg_notify`) and the LISTEN service | BE | M (1.0) | 09.2.4-T1, 09.3.2-T1 | 09.8.1-T2, 09.6.3-T1, 06.2.5-T1 (06 CR-8), 07.3.2-T1 |
| 09.8.1-T2 | `hub_tickets`, `POST /hubs/ticket`, single-use claim and cleanup job | BE | S (0.5) | 09.8.1-T1 | 09.9.3-T2, 10.2.4-T1 (10 CR-8), 10.2.4-T0 |

Both rows are **copied verbatim from README B8.1** ✅. Folder total **1.5 d raw**, matching B9 ("09.8.1 | 1.5 | 1.5 | —") — no re-estimate.

**Open items.**

1. ✅ **RULED (WT37)**: **no stamp cache in P** — no `security_stamp` column and no live stamp read; the security guarantee comes from `NOTIFY security` on every family revocation (abort that user's connections) [RULED — README §B10 WT37]. The former [OPEN] (live `users.security_stamp`/`status` read at claim and every 60 s) is dropped.
2. ✅ **RULED (WT37)**: the cap is `Hubs:MaxConnectionMinutes` = the **role's** access-token lifetime (**owner 5**, user 15) — no `token_expires_at` column on `hub_tickets`; residual exposure is at most the role's token lifetime, which matches BD3 [RULED — README §B10 WT37].
3. [OPEN] **Channel `security` is missing from BD11.** 09 L218 requires `NOTIFY` on a second channel `security` (user id only) and per-connection aborts; BD11 names only `realtime`. This plan implements both channels in the one listener (R12) — confirm BD11 should be extended, and record the sender (09.3.2-T1 `UserSecurityChanged`) as an explicit dependency of 09.8.1-T1 (B8.1 lists only `09.2.4-T1, 09.3.2-T1` ✅, so the edge exists; the *channel* does not).
4. [OPEN] **`hub` rate policy is owned by 09.3.5-T1, which 09.8.1-T2 does not depend on** (B8.1: `Depends on 09.8.1-T1`). `RequireRateLimiting("hub")` fails at runtime if the policy is not registered. Recommendation: add a soft edge `09.3.5-T1`, or have 09.8.1-T2 register the `hub` policy itself and note the move in BD1.
5. [OPEN] **`AutopilotRunUpdated` (02.8.2 L227–228)** is not among 00 §8's four events (R14) and is **not** implemented here. Decision needed: either 00 §8 gains a fifth event (then a small follow-up adds `RealtimeEventName.AutopilotRunUpdated` + the outbox handler call) or the run log keeps polling on tab focus.
6. ⚠️ **Contract shapes**: `RealtimeEvent(Name, Ids, Data?)` + the `RealtimeData` restriction are **ruled** (WT54) and ready to freeze; still to confirm: `RealtimeEventName`/`RealtimeIds` members, the exact SignalR method-name casing, and the metric names in §7. Also ⚠️ confirm that server-side SignalR needs no NuGet package (shared framework) and that the `HubTicket` authentication scheme name is acceptable.
7. [OPEN] **Group membership across instances:** with WebSockets-only there is no sticky session (R13); every instance has its own registry and groups, so a `security` abort on instance A cannot close a socket on instance B — B relies on its own backstop, i.e. the `Hubs:MaxConnectionMinutes` cap (WT37), so accept that residual, or send the `security` abort through the same `realtime` fan-out (a control message) so every instance aborts.

## 11. Rollout

- **Phase/flag:** both tasks **P · M1**, no feature flag (B8.1). 09.8.1-T1 lands before 09.6.3-T1 (B8.1 Blocks ✅), so render progress is live from the first render; 09.8.1-T2 lands before 10.2.4 needs it.
- **Migration:** one, `hub_tickets` (09.8.1-T2), expand-only, no backfill, no destructive change (D9). Rollback: drop the table (it holds only expiring tickets) — reconnecting clients simply get a 401 and the SPA retries after its next ticket request; the hub itself can stay mapped.
- **Ordering inside M1:** `09.2.4-T1` (outbox) and `09.3.2-T1` (access tokens) first; `09.3.5-T1` for policy `hub` (open item 4); `09.6.3-T1` after 09.8.1-T1.
- **Verification before "done":** the suites in §9 against local Postgres + two `ApiFactory` instances (single-use claim concurrency, cross-instance fan-out), plus one manual check: log in, open the episode page, start a render with mock-mpt and watch `RenderProgress` arrive without a refresh; then revoke the session and confirm the socket closes.
- **Observability:** the metrics in §7, the standard request logs (with the `ticket` parameter redacted) and the listener's reconnect warnings. Alerting (alerts live in 11.6.x) should page on `realtime_notify_dropped_total > 0` (a contract violation, ids-only payloads cannot exceed 8 KB) and on repeated listener reconnects.
- **Capacity:** one `LISTEN` connection per Api instance (2 in prod) plus one socket per open page; the table holds at most `10 tickets/min × connected users × 60 s` rows and is swept hourly — no sizing concern at P volume.
