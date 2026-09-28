# Terms of service 1.0.0 — coverage checklist (13.1.2-T1)

Coverage matrix for `web/src/content/legal/terms.1.0.0.vi.md` / `.en.md` and the `tos` entry of
`web/src/content/legal/manifest.json`. Sources: `plans/13-compliance.md:118-120` (§5.2 checklist, verbatim),
`plans/13-compliance.md:118-123` (§5.3 content-policy list → the inline acceptable-use summary),
`plans/features/06-youtube-posting/README.md:43` (G13: Developer Policy III.A.1), `:112` (D7/D11: `pc.v1` is 06's),
`plans/features/13-compliance/README.md` D1/D2/D3/O1, `13.1-legal-acceptance/plan.md` §2/§3/§5–§7,
`plans/12-billing-credits.md:42,45,142-143,170-172` (credits, expiry, refunds).
Legend: covered · inline in `1.0.0`, link at `1.1.0` · deferred · n/a — no row is empty.
⚠️ = lawyer-review item (13.1.2-T2, S; O1 personal version).

## 1. §5.2 item to slot map

| §5.2 item (source) | Slot | vi heading | en heading | Status |
|---|---|---|---|---|
| Intro: what these terms cover, contact, and acceptance as a condition of use with the connect/autopilot gate (not in §5.2's own list; source 13 README D3 + 13.1.2-T1 §Design slot 1) | 1 | Giới thiệu các điều khoản này và cách liên hệ | About these terms and how to contact us | covered — operator name/contact **placeholders**, see §4 OI-1; acceptance is stated as the condition of use and the gate is named (connect + autopilot) |
| Eligibility (18+) ⚠️ lawyer (§5.1 item 10 [DECISION]; 13.2.3-T1 is the S age gate) | 2 | Điều kiện sử dụng | Eligibility | covered — 18+, capacity to contract, account closed if a minor is found; ⚠️ stays with 13.1.2-T2/13.2.3-T1 |
| Account security (one person per account, credentials, sessions, report misuse) | 3 | Tài khoản của bạn và bảo mật tài khoản | Your account and its security | covered — single-user account, no sharing, unused-elsewhere password, session revocation on suspected compromise, notify-us duty |
| Credits: non-cash, expiry per 12.6.2 ([DECISION] 12 months ⚠️ lawyer) | 4 | Tín dụng, thời hạn sử dụng và hoàn tiền | Credits, expiry and refunds | covered — non-cash, non-transferable; purchased + bonus **12 months from payment** (never from inactivity), trial **30 days**, promo per code; ⚠️ expiry/consumer-law row in §4 OI-2 |
| Credits: refunds per 12.7.5 incl. the spent-bonus deduction (FR-12-14, 12 CR-6) | 4 | (same heading) | (same heading) | covered — **14-day** window for unused purchased credits, refundable = unused purchased of that payment − bonus of that payment already spent, all lots of a refunded payment voided; admin decision with a stated reason; ⚠️ in §4 OI-2 |
| Acceptable use → **link** Content Policy | 5 | Sử dụng được phép | Acceptable use | **inline in `1.0.0`, link at `1.1.0`** — §5.3 prohibited/restricted summary inline, no `/content-policy` route exists in P (13.1.3 is S, see §7) |
| Ownership (user owns outputs subject to provider terms ⚠️ check OpenAI terms) | 6 | Quyền sở hữu nội dung bạn tạo | Ownership of what you create | covered — user owns the story text and the approved episode; OpenAI named as the provider whose terms govern the generated assets (the service claims no ownership of the outputs); add-your-own-material rights duty; ⚠️ the output-ownership sentence must be confirmed against the current OpenAI terms of use (§5.2's own "check OpenAI terms on output ownership"), see §4 OI-2 |
| AI disclosure duty (13 §FR-13-7) | 7 | Nội dung do AI tạo và nghĩa vụ công bố | AI-generated content and disclosures | covered — uploads are marked as synthetic media, the user keeps the platform disclosure and self-declares when the platform asks, and reviews the episode before publishing |
| No monetisation guarantee | 8 | YouTube và các nền tảng được kết nối khác | YouTube and other connected platforms | covered — no promise that a platform monetises, promotes, keeps or accepts an episode; quota/limits may delay |
| User is the publisher of record | 8 | (same heading) | (same heading) | covered — "you are the publisher of record"; the channel and its content are the user's, with the platform rules and consequences |
| Platform rules compliance (YouTube/TikTok) | 8 | (same heading) | (same heading) | covered — both platforms named; the service may refuse an upload and a platform may act independently |
| Takedown and suspension | 10 | Gỡ nội dung, tạm ngưng và chấm dứt | Takedown, suspension and termination | covered — suspension/closure on breach or legal duty, publishing features stopped after a platform report, the user may stop at any time; data handling is a **pointer** to the privacy policy's retention section (§5 tests: no restated windows) |
| Service availability | 9 | Tính khả dụng của dịch vụ và các thay đổi | Service availability and changes | covered — best-effort, maintenance, feature changes, third-party dependency, no SLA/uptime target in the personal phase |
| Liability limits | 11 | Giới hạn trách nhiệm và tuyên bố miễn trừ | Liability limits and disclaimers | covered — no fitness/acceptance/accuracy warranty; cap at the amounts paid in the 12 months before the event; no limitation where the law forbids it; ⚠️ wording with 13.1.2-T2 |
| Governing law/disputes ⚠️ lawyer | 12 | Luật áp dụng, tranh chấp và thay đổi các điều khoản này | Governing law, disputes and changes to these terms | covered — Vietnamese law and courts, unless mandatory consumer law gives another forum; the operator's current practice, not a settled legal position; also carries the changes clause (new version + effective date, re-acceptance before connect/autopilot; 13 README D1/O1); ⚠️ row in §4 OI-2 |

Also required by 13 README D1 (terms clause list) and by 12.6.2 ("the rules … are shown on the pricing page and in the terms"):
credit expiry, the 14-day refund window and the spent-bonus deduction — all three are in slot 4, rows above.

## 2. YouTube Developer Policy rows (G13, `06-youtube-posting/README.md:43`)

| Policy requirement | Slot | Status / evidence |
|---|---|---|
| III.A.1 — link the YouTube Terms of Service | 8 | covered — `https://www.youtube.com/t/terms` linked in both locales (checked by `legal-parity.py`) |
| III.A.1 — the terms must **bind** the user to the YouTube ToS (a bare link is not enough) | 8 | covered — en "you agree to be bound by the [YouTube Terms of Service]"; vi "bạn đồng ý bị ràng buộc bởi [Điều khoản dịch vụ của YouTube]" |
| III.A.1/III.A.2 — the connected-channel features are the trigger, and the API-services data duties are surfaced | 8 | covered — the clause is scoped to "using the channel features" and the data/revocation duties point at the privacy policy (`/en/privacy`, `/vi/privacy`) instead of restating the data list (13 README D2) |

The privacy-side obligations (III.A.2 statement/links, III.D.2 windows, III.E.4 refresh/delete) are covered by
`privacy-checklist-1.0.0.md` §2; this document only points at that policy.

## 3. Markdown-subset, manifest and cross-file checks (build-time contract, 10 README D12 / 10.3.1-T1 §Design)

| Rule | Status |
|---|---|
| One `#` title, no front matter, no HTML comments | covered |
| Numbered `## N.` headings, identical numbers in both locales | covered — 1..12 in both |
| Subset only: headings, paragraphs, `-` lists, `**emphasis**`, `[text](href)` links whose target is `https://`, `mailto:` or site-relative `/` | covered — no raw HTML, no table, no image, no footnote, no nested list, no bare URL, no `http:`, no unknown scheme |
| No version literal and no effective date in the prose (the page renders both from the manifest) | covered — the prose states no `x.y.z` version and no ISO date |
| Manifest carries `tos` (`privacy` is 13.1.1-T1's): `{"tos": {"version": "1.0.0", "effectiveDate": "2026-10-01"}}` | covered — `manifest.json` has exactly `privacy` + `tos`, both `1.0.0`; the CI check needs both keys (I1, 13 CR-30) |
| Cross-file non-duplication: the retention windows stay in the privacy policy, and `pc.v1` stays with 06 | covered — neither terms file contains `7 days` / `30 days` / `7 ngày` / `30 ngày` (only slot 10's pointer sentence); the terms describe the acceptance model without quoting `pc.v1` |

## 4. Owner open items (not closable by this task)

| Id | Item | Why / owner action |
|---|---|---|
| OI-1 | Operator legal name and contact address are **placeholders** in both locales (slot 1) | the repo states neither; nothing is invented. The owner must set both before the service is used by anyone other than the operator (O1) |
| OI-2 | ⚠️ rows: the credit expiry and refund rules (consumer law), the output-ownership sentence (confirm against the current OpenAI terms of use), governing law/disputes, liability cap, and the 18+ eligibility | stay ⚠️ until 13.1.2-T2 (lawyer review, S). The rows above are the list to close |
| OI-3 | The credit/refund sentences must stay equal to `plans/12-billing-credits.md` 12.6.2/12.7.5 and to the implemented billing UI | re-read at the M1 filing (13.4.2-T1 D5 walkthrough); a duplicated or drifted number here is a policy-vs-behaviour finding. This is why the terms cite 12 and 13 instead of restating their numbers |
| OI-4 | The Content Policy link is a `1.1.0` deferral (13.1.3, S) | when 13.1.3 lands, add the link to slot 5 and bump both locales + the manifest — never edit `1.0.0` in place |
| OI-5 | No cookie sentence in the terms | 13.1.4 (S) owns the cookie notice; the privacy policy is the only notice. Revisit only if 13.1.4 adds a cross-reference requirement |

## 5. Parity check (repeatable; run from the repo root)

One script covers both documents (13.1.1-T1 and 13.1.2-T1) — `docs/compliance/legal-parity.py`, stdlib only:

```bash
python3 docs/compliance/legal-parity.py            # from the repo root
python3 docs/compliance/legal-parity.py /tmp/copy  # or any checkout root
```

It exits 0 (printing `OK <doc> <version> <n>/<n>` per document), 1 on a named failure, 2 on a usage error. Checks:
the manifest contract; that each document's `vi` and `en` files exist at the *same* version as the manifest (a `vi`≠`en`
version fails); one `#` title and no front matter; the `## N.` sets are identical and exactly `1..10` / `1..12`; the
required links (both locales); the III.A.1 binding sentence; the per-slot block structure (paragraphs and top-level
bullets) equal in both locales; the Markdown-subset rejection; no version literal or effective date in the prose; the
`pc.v1` citation (privacy); and no restated retention window (terms). The bullet counts the acceptance checks name are
asserted too (privacy slot 5 = 6, terms slot 4 = 4), so a translation that drops a bullet fails.

Expected output on this commit:

```
OK privacy 1.0.0 10/10
OK tos 1.0.0 12/12
```

The same checks as the short commands the task file names (`doc = terms`, `ver = 1.0.0`):

- Heading parity: `grep -E '^## [0-9]+\.' web/src/content/legal/terms.1.0.0.en.md | sed -E 's/^## ([0-9]+)\..*/\1/' | tr '\n' ' '` against the `.vi.md` file — both print `1 2 3 4 5 6 7 8 9 10 11 12 `.
- III.A.1 presence: `grep -c 'https://www.youtube.com/t/terms'` — 1 in each locale; `grep -c 'bound by'` (en) and `grep -c 'ràng buộc'` (vi) ≥ 1.
- Manifest contract: `python3 -c "import json;d=json.load(open('web/src/content/legal/manifest.json'));print(sorted(d),d['tos']['version'],d['privacy']['version'])"` → `['privacy', 'tos'] 1.0.0 1.0.0`.
- Subset rejection: `grep -nE '<[a-zA-Z/!]|^\|' web/src/content/legal/terms.1.0.0.*.md` — no output.
- Cross-file non-duplication: `grep -nE '7 days|30 days|7 ngày|30 ngày' web/src/content/legal/terms.1.0.0.*.md` — no output.

## 6. Review steps (recorded, not automated)

1. Read the vi file start to finish, then the en file: a sentence present in one and missing in the other is a finding.
2. Open `https://www.youtube.com/t/terms` (done 2026-09-29) and check the III.A.1 clause binds the user, not just links.
3. Verify every §5.2 row of §1 is either covered or explicitly deferred, and that no ⚠️ item reads as a settled position in the text.
4. Read slot 8 against 06 README D7/D11 (`pc.v1` is 06's) and slot 4 against `plans/12-billing-credits.md` 12.6.2/12.7.5.
5. Re-run §5 and the manifest command; then hand the files to 10.3.1-T1 (pages), 13.2.1-T1 (acceptance dialog) and 13.1.2-T2 (lawyer review).

**Executed 2026-09-29 (this commit).** (1) Both locales were read end to end; the per-slot structure and the
number/URL multisets are identical, so no sentence exists in one locale only. The deliberate asymmetries are by design:
the Vietnamese file explains the platform wording that stays official in English (credit, autopilot, synthetic media,
publisher of record). (2) `https://www.youtube.com/t/terms` was opened (200) and slot 8 binds the user, not just links.
(3) Every §5.2 row is covered or explicitly deferred (§1/§7), and no ⚠️ item is asserted as final: slot 4 ends with the
stronger-right sentence, slot 11 is "to the extent the law allows", slot 12 keeps the mandatory-consumer-law forum.
(4) Slot 8 was read against 06 README D7/D11 and slot 4 against 12.6.2/12.7.5/FR-12-14. (5) `legal-parity.py` exits 0
(`OK privacy 1.0.0 10/10`, `OK tos 1.0.0 12/12`); the manifest command prints `['privacy', 'tos'] 1.0.0 1.0.0`.

Link resolution (opened 2026-09-29 with a plain HTTP client; canonical targets observed):

| URL | Observed |
|---|---|
| `https://www.youtube.com/t/terms` | 200, page titled "Các Điều Khoản Dịch Vụ" (YouTube Terms of Service, localised by the requester's locale) ✅ |
| `https://policies.google.com/privacy` | 200, "Privacy Policy – Privacy & Terms – Google" ✅ (privacy policy, slot 3) |
| `https://security.google.com/settings/security/permissions` | 301 → `https://myaccount.google.com/permissions`, which 302s to `https://myaccount.google.com/general-light` and, unauthenticated, to the Google sign-in page ✅ — the canonical permissions page named by III.A.2 (privacy policy, slot 4) |

No link 404s at the time of writing, so no `[OPEN]` link item is raised for 13.1.2-T2. The permissions page requires a
Google sign-in, which is expected for a per-account permissions screen; nothing in the text promises that it opens
without a session.

## 7. Deferred to a later version (recorded, not dropped)

| Item | Why |
|---|---|
| Link to the standalone Content Policy page | 13.1.3 is S and no `/content-policy` route exists in P; `1.0.0` carries the inline summary and no link. Adding the link is a version bump when 13.1.3 lands |
| Cookie-notice cross-reference | 13.1.4 is S; the terms mention no cookie (the privacy policy is the only notice) |
| Lawyer review (13.1.2-T2): liability wording, governing law, credit expiry/refund enforceability, the 18+ clause, OpenAI's output terms | O1: the personal version ships while the owner is the only user; the review runs before any other user, which is when these rows close |
| Anything the Vietnamese data-protection memo (13.6.1) adds to the consumer/legal wording | 13.6.1 is S; until it lands no local-law claim appears in this text (13 README D16) |
