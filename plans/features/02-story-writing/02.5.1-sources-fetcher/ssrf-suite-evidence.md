# ssrf-suite-evidence - 02.5.2-T4 gate evidence

Files: `S` = `tests/NightTales.Api.IntegrationTests/Features/Research/FetcherSsrfSuiteTests.cs`, `A` = `.../FetcherAbuseAndCapsTests.cs`, `F` = `.../FetcherLeakAssertionsFixture.cs`.

## (a) Threat-table row / case -> test -> observed result

| Threat row | Case | Test member (file:line) | Observed result |
|---|---|---|---|
| 1 | 1 | `Ssrf_LoopbackAddress_Blocked_AndServerSeesNoRequest` (S:58) | literal `127.0.0.1:{port}` -> `source_url_invalid`; name -> loopback -> `source_blocked_by_robots` (F-T4-1); server 0 connections, 0 requests |
| 1 | 2 | `Ssrf_PrivateAddresses_Blocked` (S:72) | literals 10.0.0.1 / 172.16.0.1 / 192.168.1.1 -> `source_url_invalid`; names -> `source_blocked_by_robots`; 0 requests |
| 2 | 3 | `Ssrf_MetadataAddress_Blocked` (S:82) | literal 169.254.169.254 -> `source_url_invalid`; name -> `source_blocked_by_robots`; 0 requests |
| 1 | 4 | `Ssrf_CgnatAndZeroAddress_Blocked` (S:92) | literals 100.64.0.1, 0.0.0.0 -> `source_url_invalid`; names (also 192.0.0.8, 198.18.0.1, 240.0.0.1) -> `source_blocked_by_robots`; 0 requests |
| 3 | 5 | `Ssrf_IpLiteralDecimalOctalHex_RejectedBeforeDns` (S:116) + `..._FetcherNeverResolves` (S:129) | `SourceUrl.Parse` -> `source_url_invalid`; the fetcher-side twin asserts the resolver saw 0 lookups |
| 3 | 6 | `Ssrf_Ipv4MappedAndCompatibleV6_Blocked` (S:145) | `source_url_invalid`; 0 requests |
| 1 | 7 | `Ssrf_SixToFourAndTeredo_Blocked` (S:157) | `source_url_invalid`; 0 requests |
| 1 | 8 | `Ssrf_UlaLinkLocalSiteLocalMulticastV6_Blocked` (S:170) | `source_url_invalid`; 0 requests |
| 1 | 9 | `Ssrf_Nat64AndDocumentationV6_Blocked` (S:181) | `source_url_invalid`; 0 requests |
| 4 | 10 | `Ssrf_DnsRebinding_PublicAnswerWhileServerOnLoopback_NoConnectionAndUnreachable` (S:190) | public answer: 0 connections, 0 requests, code `source_blocked_by_robots` (robots connect refused first, D11); mixed `[public, 127.0.0.1]` under production validator: 0 connections |
| 4 | 10 (converse) | `Ssrf_DnsResolverAnswerLoopback_ExactlyOneRequestReachesServer` (S:211) | resolver answers 127.0.0.1 -> exactly 1 page request reaches the server |
| 4 | 10 (defence in depth) | `Connect_PeerAddressDeniedByScriptedValidator_Throws` (S:224) | scripted validator denies the connected peer (RemoteEndPoint re-check) -> no page request; case 10 does not depend on this |
| 3, 13 | 11 | `Url_IdnAndTrailingDot_NormalisedNotBlocked` (S:241) | accepted and normalised (punycode / trailing dot stripped) |
| 3 | 12 | `Url_SingleLabelAndBareTld_Rejected` (S:263) | `source_url_invalid` |
| 3 | 13 | `Url_PortUserInfoSchemeLength_Rejected` (S:278) | `source_url_invalid` |
| 13 | 14 | `InternalApi_LeaseRouteAbsentOnPublicPort` (S:290) | 404 on the public port |
| 5 | 15 | `Redirect_ToLoopback_Blocked` (S:309) | `source_url_invalid`; loopback `/ok` 0 requests |
| 5 | 16 | `Redirect_ToMetadata_Blocked` (S:322) | `source_url_invalid` |
| 11 | 17 | `Redirect_ToBlockedDomain_Blocked` (S:338) | `source_domain_not_allowed` for m.youtube.com, youtu.be, vm.tiktok.com, bit.ly (bit.ly added to the test options, see (e)) |
| 5 | 18 | `Redirect_FourHops_Unreachable` (S:351) | `source_unreachable`; server logs the exact request count |
| 5 | 19 | `Redirect_RelativeLocation_FollowedAndRevalidated` (S:362) | followed; success |
| 5 | 20 | `Redirect_WithCredentialsOrBadScheme_Rejected` (S:378) | `source_url_invalid` |
| 7 | 21 | `Payload_GzipBomb_TooLarge` (A:48) | `source_too_large`; bytes written bounded |
| 8 | 22 | `Payload_WrongOrDisguisedContentType_Unsupported` (A:62) | `source_unsupported_type` |
| 8 | 23 | `Payload_SpaAndShortText_NoText` (A:73) | `source_no_text` |
| 8 | 23b | `Payload_MinTextCharsBoundary` (A:82) | 1,274 chars -> `source_no_text`; 1,275 -> success |
| 8 | 23c | `Payload_DeepNestedHtml_DepthGuard_TooLarge_NoCrash` (A:114) | `source_too_large`; no crash, inside the budget |
| 8 | 23d | `Payload_ManyElements_ElementGuard_TooLarge` (A:126) | `source_too_large` |
| 8 | 24 | `Payload_OverMaxChars_TooLarge` (A:138) | `source_too_large` |
| 9 | 25 | `Robots_Disallow_BlockedByRobots` (A:149) | `source_blocked_by_robots`; only robots + page requests |
| 9 | 26 | `Robots_ServerErrorOrTimeout_BlockedByRobots` (A:162) | `source_blocked_by_robots` for 5xx and timeout |
| 9 | 27 | `Robots_NotFound_AllowsAll` (A:177) | success |
| 9 | 28 | `Robots_Cached24h_OneFetchPerOrigin` (A:189) | 1 robots request; 2nd after +25 h |
| 10 | 29 | `Cookies_SetByServer_NotReplayed` (A:206) | no `Cookie` header on the 2nd request |
| 10 | 30 | `Timeout_SharedBudget_Unreachable` (A:219) | `source_unreachable` inside the shared budget |
| 10 | 31 | `RateLimit_OnePerSecondPerDomain_NotAcrossDomains` (A:232) | same domain >= 1 s apart; different domains not serialised |
| 11 | 32 | `Caps_ThirtyFirstUrlSource_429_AndEleventhSourceOnEpisode_409` (A:262) | 429 `source_fetch_limit_reached` + Retry-After; 409 `source_limit_reached` |
| 17, 18 | 33 | `Lease_ConcurrentCalls_PlatformAndDomainCapsHold` (A:297) | <= 4 leased, no duplicate task, <= 1 per domain; 20 iterations |
| 18 | 34 | `Lease_AbandonedTask_SweeperRequeuesThenFails` (A:338) | requeued, then task + source `failed source_unreachable` |
| D19 | cross | `Leak_NoUrlTitleOrTextInLogsOrTraces` (A:381) | fetcher log + trace scan clean |
| D19 + persistence | cross | `Persistence_ExtractedTextObjectHasNoHtml_FailedCaseWritesNone_ApiLogsDoNotLeak` (A:412) | real loop + Api: object has no `<`/`>`, length == `text_chars`; failed source has no key, no extra `research/` object; Api and fetcher logs carry no path, query, title or text |
| D8 | cross | `ErrorCodes_AreInCatalogue` (A:468) | every observed code is in the job-code set |
| count | cross | `Suite_HasAll37Cases` (A:484) | 37 case ids + extras present (rows in F) |
| D20 | 1,2,3,6,15,16,18,19,25 shapes | `D20_ProductionValidator_LoopbackServerIsUnreachable` (S:404, 11 rows) | production validator: server 0 connections, 0 requests; mode asserted `GlobalUnicast` |

## (b) 02 section 7 clause -> test

| 02 section 7 clause | Test |
|---|---|
| private IPs | `Ssrf_PrivateAddresses_Blocked` |
| IPv4-mapped IPv6 private IPs | `Ssrf_Ipv4MappedAndCompatibleV6_Blocked` |
| `0.0.0.0` | `Ssrf_CgnatAndZeroAddress_Blocked` |
| DNS rebinding (public then private -> blocked at connect) | `Ssrf_DnsRebinding_PublicAnswerWhileServerOnLoopback_NoConnectionAndUnreachable` |
| redirect-to-private | `Redirect_ToLoopback_Blocked`, `Redirect_ToMetadata_Blocked` |
| oversize | `Payload_GzipBomb_TooLarge`, `Payload_OverMaxChars_TooLarge` |
| wrong type | `Payload_WrongOrDisguisedContentType_Unsupported` |
| blocked domains | `Redirect_ToBlockedDomain_Blocked` |
| robots.txt disallow | `Robots_Disallow_BlockedByRobots` |
| 31st fetch in 24 h -> `source_fetch_limit_reached` | `Caps_ThirtyFirstUrlSource_429_AndEleventhSourceOnEpisode_409` |

## (c) F14 gap -> test

| # | F14 gap | Test |
|---|---|---|
| 1 | decimal / octal / hex literals | `Ssrf_IpLiteralDecimalOctalHex_RejectedBeforeDns` |
| 2 | `192.0.0.0/24` | `Ssrf_CgnatAndZeroAddress_Blocked` (proto-e2e.com -> 192.0.0.8) |
| 3 | `198.18.0.0/15` | `Ssrf_CgnatAndZeroAddress_Blocked` (bench-e2e.com -> 198.18.0.1) |
| 4 | `240.0.0.0/4` | `Ssrf_CgnatAndZeroAddress_Blocked` (reserved-e2e.com -> 240.0.0.1) |
| 5 | `2001::/32` | `Ssrf_SixToFourAndTeredo_Blocked` |
| 6 | `fc00::/7` | `Ssrf_UlaLinkLocalSiteLocalMulticastV6_Blocked` |
| 7 | `ff00::/8` | `Ssrf_UlaLinkLocalSiteLocalMulticastV6_Blocked` |
| 8 | IDN / trailing dot | `Url_IdnAndTrailingDot_NormalisedNotBlocked` |
| 9 | subdomain and redirect-target blocklist | `Redirect_ToBlockedDomain_Blocked` |
| 10 | decompression bomb | `Payload_GzipBomb_TooLarge` |
| 11 | cookies | `Cookies_SetByServer_NotReplayed` |
| 12 | robots failure semantics | `Robots_ServerErrorOrTimeout_BlockedByRobots`, `Robots_NotFound_AllowsAll` |

## (d) Run summary

| Item | Value |
|---|---|
| Command | `TEST_FILTER='FullyQualifiedName~Fetcher' gate.sh` (Api.IntegrationTests + Fetcher unit tests that match) |
| Run 1 (after review fixes) | total 341, passed 341, failed 0, skipped 0; test duration 16.9 s |
| Run 2 (repeat) | total 341, passed 341, failed 0, skipped 0; test duration 17.0 s |
| Earlier runs | implementation 299/299 twice (16.2 s / 16.1 s); QA: 299/299 twice, 307/307 with its attribution test |
| QA mutations | deleting the `RemoteEndPoint` re-check leaves case 10 green and fails the defence-in-depth test (Acceptance 131); deleting `10.0.0.0/8` is caught only by `FetcherAddressRuleAttributionQaTests` (now covers every v4/v6 block row) |
| Limit | < 3 min: met |
| Validator mode | D20 test sends a diagnostic message `D20 run: validator=GlobalUnicast, url=...` and asserts `stack.Mode == GlobalUnicast`; the quiet gate log does not show diagnostic messages |
| Build-only gate | `BUILD+FORMAT OK` |

## (e) Deviations / findings (TL ruling, Session 12, against README D8/D11)

| Id | Finding | Ruling |
|---|---|---|
| F-T4-1 | A host name resolving to a non-global address (extra variants beyond the task table; cases 1-4 as written use IP literals and give `source_url_invalid`) ends `source_blocked_by_robots`. Path: connector skips the denied address and throws `source_unreachable` (`SafeHttpConnector.cs` connect loop) -> `RobotsTxtPolicy.LoadAsync` catch maps it to `robots_unreachable` deny (`RobotsTxtPolicy.cs`) -> `SafeHttpSourceFetcher.cs:125-129` throws `source_blocked_by_robots`. IP literals give `source_url_invalid` (Parse and `SafeHttpConnector.cs:41-47`). | Accepted, no product change: D11 says robots "5xx/unreachable → disallow (`source_blocked_by_robots`)" and D8 `source_url_invalid` covers IP literals, not resolved names. Security property (zero requests to a non-global address) holds. |
| Case 10 variance | Mixed answer `[93.184.216.34, 127.0.0.1]` reaches loopback under the loopback-allowing validator (127.0.0.1 is allowed there by D20 design); that sub-case runs under the production validator (0 connections). | Accepted: the property is proven on the production graph; no test passes only with the test validator. |
| bit.ly config | `Fetcher:BlockedDomains` in `src/NightTales.Fetcher/appsettings.json` and `src/NightTales.Api/appsettings.json` holds 19 entries, no `bit.ly`. Suite adds `bit.ly` to its test options; case 17 proves the per-hop logic, not the default list. | Accepted, config unchanged: the README D11 list (wins) has exactly these 19 entries; `plan.md` "20 entries" was a stale count, corrected to 19. Adding shorteners is a D11 data change via its own PR. |
| D8 set | `source_url_invalid` is in the suite's job-code set (the fetcher returns it for invalid redirect hops). | Accepted: it is in the README D8 catalogue and the task table expects it from the fetcher (cases 15, 16, 20). |
| Case 10 code (QA F4) | Acceptance 130 names `source_unreachable` for the public answer; the robots fetch dials the (refused) validated address first, so the observed code is `source_blocked_by_robots`. | Accepted: same D11 robots fail-closed rule as F-T4-1; Acceptance 130's property ("zero connections", plan.md §8 row 4) holds and is what the test pins. |
| Validator print (QA F6) | The D20 diagnostic message is not visible in the quiet gate log. | Accepted: the in-test assertion `stack.Mode == GlobalUnicast` is the guard. |
| Resolver log (QA F7) | No suite-wide "never a public address" assertion; per-case DNS-log checks only. | Accepted: real egress is impossible by construction (`HttpTestServer.EndpointFor` maps every non-loopback address to a closed local port; DNS is always `FakeDnsResolver`). |
| Count granularity (QA F8) | `Suite_HasAll37Cases` counts case rows and catches a deleted test method, not a deleted `InlineData` row. | Accepted. |
| Case 30 wait (perf) | ~10 s real wait from the task-pinned `/slow?ms=12000` and 10 s budget. | Kept: the numbers are pinned by the task file; the suite stays well under 3 min. |
| Case 33 iterations (perf) | 20 iterations (~1 s). | Kept: deliberate repeat for the concurrency invariant. |
