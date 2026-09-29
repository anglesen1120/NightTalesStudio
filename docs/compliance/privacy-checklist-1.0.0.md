# Privacy policy 1.0.0 — coverage checklist (13.1.1-T1)

Coverage matrix for `web/src/content/legal/privacy.1.0.0.vi.md` / `.en.md` and the `privacy` entry of
`web/src/content/legal/manifest.json`. Sources: `plans/13-compliance.md:105-116` (§5.1 checklist, verbatim),
`plans/features/06-youtube-posting/README.md:43` (G13: Developer Policies III.A.2 / III.D.2 / III.E.4 / user deletion),
`plans/features/13-compliance/README.md` D1/D2/O1, `13.1-legal-acceptance/plan.md` §2/§3/§5.
Legend: covered · deferred · n/a — no row is empty. ⚠️ = lawyer-review item (13.1.1-T2, S; O1 personal version).

## 1. §5.1 item to slot map

| §5.1 item (source) | Slot | vi heading | en heading | Status |
|---|---|---|---|---|
| Who we are, contact for privacy questions/complaints ✅ | 1 | Chúng tôi là ai và cách liên hệ về quyền riêng tư | Who we are and how to contact us | covered — operator legal name and privacy contact are **placeholders**, see §4 OI-1; complaint path to the competent authority is stated |
| Data collected: account, content created, channel connection data (YouTube/TikTok ids, names, tokens), usage/billing, logs | 2 | Dữ liệu chúng tôi thu thập | Data we collect | covered — five bullets; billing rows flagged as appearing only after billing is enabled |
| Data from YouTube API Services: accessed / stored / shared / how used ✅; "uses YouTube API Services" statement; YouTube Terms link and Google Privacy Policy link ⚠️ | 3 | Dữ liệu từ YouTube API Services | Data from YouTube API Services | covered — literal sentence "NightTalesStudio uses YouTube API Services" (vi: "sử dụng YouTube API Services"); four bullets (access/store/share/use); both links present as Markdown links |
| How users revoke access: in-app disconnect and `https://security.google.com/settings/security/permissions` ✅ | 4 | Cách bạn thu hồi quyền truy cập | How you revoke access | covered — two bullets, the Google permissions page linked in both locales; "published video stays on YouTube" caveat stated |
| Retention and deletion: 7/30-day windows by trigger; 30-day refresh/delete of Authorized Data ✅; backups kept [DECISION] 30 days, so deleted data ages out of the backups within that window, and a restore re-applies every deletion completed after the backup date before the service reopens | 5 | Thời gian lưu trữ và xoá dữ liệu | Retention and deletion | covered — six bullets, bullet list (no table): 7 / 30 / 30-day refresh / 7 / 30-day backups (a restore re-applies every deletion completed after the backup date before the service reopens) / pseudonymised retention. The last bullet names every kept row of `plans/13-compliance.md` §5.4: Episode scope (metered usage rows kept for cost accounting), Channel scope (publication rows without external ids), and Account scope (credit/payment rows as accounting law requires, plus the security/staff-action audit rows and the acceptance records), with the user reference pseudonymised in the account case |
| Upload consent: one record per upload; autopilot derived from the versioned series authorization after the veto window (06.2.2) | 6 | Sự đồng ý khi đăng tải | Upload consent | covered — per-upload record + veto-window derivation with edit/cancel; cites `pc.v1` (`Templates/Consent/pc.v1.vi.md`, `.en.md`) and does not restate its text (13 README D2) |
| We never ask for YouTube passwords ✅ | 7 | Chúng tôi không bao giờ hỏi mật khẩu YouTube của bạn | We never ask for your YouTube password | covered — statement plus "anyone asking is not acting for us" |
| Sub-processors: OpenAI, hosting, storage, payment provider, e-mail provider | 8 | Các bên xử lý dữ liệu thay chúng tôi | Sub-processors | covered — five bullets; only OpenAI is named (the others are categories, no provider name is stated in the repo) |
| Staff access: account/billing/session data for support and abuse; story text/sources/videos only inside a moderation or report case; every view logged (09.10.5); no impersonation; staff/security alerts internal only | 9 | Quyền truy cập của nhân sự, ghi log và cảnh báo bảo mật | Staff access, logging and security alerts | covered — five bullets mirroring the item, incl. "no impersonation" and alerts "never shared with third parties" |
| International transfers, security measures, user rights (access, correction, deletion, export), children (not under-18 ⚠️ lawyer), changes to the policy | 10 | Chuyển dữ liệu ra nước ngoài, biện pháp bảo mật, quyền của bạn, trẻ em và thay đổi chính sách | International transfers, security, your rights, children and changes | covered — five bullets; 18+ stated, transfers rely on provider contractual safeguards; ⚠️ (see §4 OI-2) |

## 2. YouTube Developer Policy rows (G13, `06-youtube-posting/README.md:43`)

| Policy requirement | Slot | Status / evidence |
|---|---|---|
| III.A.2 — a privacy policy linked/surfaced before the YouTube features are used | 3 (+ pages) | covered — the policy is a public prerendered page (10.3.1-T1 renders `/vi/privacy`, `/en/privacy`) and 13.2.1-T1's acceptance dialog links it before the connect flow; this task supplies the content and the link targets |
| III.A.2 — the statement that the service uses YouTube API Services | 3 | covered — literal sentence "NightTalesStudio uses YouTube API Services" (en) / "NightTalesStudio sử dụng YouTube API Services" (vi) |
| III.A.2 — link to the Google Privacy Policy | 3 | covered — `https://policies.google.com/privacy` linked in both locales |
| III.A.2 — link to the YouTube Terms of Service | 3 | covered — `https://www.youtube.com/t/terms` linked in both locales |
| III.A.2 — link to the page where the user revokes access | 4 | covered — `https://security.google.com/settings/security/permissions` linked in both locales |
| III.D.2 — 7 days after in-app revoke, 30 days after Google-page revoke, tokens reconfirmed periodically | 5 | covered — bullets 1 and 2 (7 / 30 days); periodic reconfirmation is the same job that enforces the 30-day refresh in bullet 3 |
| III.E.4 — reconfirm authorization / refresh or delete stored Authorized Data every 30 days | 5 | covered — bullet 3 |
| III.E.4 — user deletion completed within 7 days | 5 | covered — bullet 4 |
| III.A.2 — the stored Authorized Data must not be kept beyond what is needed | 5 | covered — bullets 3, 5 and 6 (refresh/delete, 30-day backups, pseudonymised retention) |

Link resolution (opened 2026-09-29 with a plain HTTP client; canonical targets observed, no 404):

| URL | Observed |
|---|---|
| `https://www.youtube.com/t/terms` | 200, page titled "Các Điều Khoản Dịch Vụ" (YouTube Terms of Service; the title is localised by the requester's locale) ✅ |
| `https://policies.google.com/privacy` | 200, page titled "Privacy Policy – Privacy & Terms – Google" ✅ |
| `https://security.google.com/settings/security/permissions` | 301 → `https://myaccount.google.com/permissions`, which 302s to `https://myaccount.google.com/general-light` and, unauthenticated, to the Google sign-in page ✅ — the canonical permissions page named by III.A.2; the sign-in is expected for a per-account screen |

## 3. Markdown-subset and manifest checks (build-time contract, 10 README D12 / 10.3.1-T1 §Design)

| Rule | Status |
|---|---|
| One `#` title, no front matter, no HTML comments | covered |
| Numbered `## N.` headings, identical numbers in both locales | covered — 1..10 in both |
| Subset only: headings, paragraphs, `-` lists, `**emphasis**`, `[text](href)` links | covered — per-line allow-list (`# ` title, `## N.`/`### ` headings, top-level `- `/`1. ` items, `> ` quotes, paragraphs, blanks); no raw HTML or autolink, no fenced/indented code, no reference-style link definition, no table, no image, no footnote, no nested list (`> - x`), no theme break/Setext rule, no bare URL, no non-`https:` link scheme |
| No version literal and no effective date in the prose (the page renders them from the manifest) | covered — neither file contains the version string or a date |
| Manifest entry `{"privacy": {"version", "effectiveDate"}}`; the `tos` key is 13.1.2-T1's edit | covered — `manifest.json` carries exactly `privacy` + `tos`, both version `1.0.0` with `effectiveDate` `2026-10-01`. 13.1.1-T1 alone wrote `privacy`; both keys are required once 13.1.2-T1 lands (I1, 13 CR-30) |

## 4. Owner open items (not closable by this task)

| Id | Item | Why / owner action |
|---|---|---|
| OI-1 | Operator legal name and privacy contact address are **placeholders** in both locales (slot 1) | the repo states neither; no name, address or e-mail is invented. The owner must set both before the policy is shown to anyone other than the operator (O1) |
| OI-2 | ⚠️ children / 18+, international transfers, retention wording | stay ⚠️ until 13.1.1-T2 (lawyer review, S). The ⚠️ rows above are the list to close |
| OI-3 | The retention bullets must stay equal to `plans/13-compliance.md` §5.4 and to the deletion engine (13.3.1-T1) | re-read at the M1 filing (13.4.2-T1 D5 walkthrough); a divergence is a policy-vs-behaviour finding |
| OI-4 | Version bump path | a later change ships `privacy.1.1.0.<lang>.md` + a manifest bump (never edit `1.0.0` in place); enforcement is 13.2.2-T1's `tools/ci/check-legal-versions.py` |

## 5. Parity check (repeatable; run from the repo root)

One stdlib script covers both documents of this folder — `docs/compliance/legal-parity.py`
(the same file is named by `terms-checklist-1.0.0.md` §5, so the privacy and terms checks cannot drift apart):

```bash
python3 docs/compliance/legal-parity.py            # from the repo root
python3 docs/compliance/legal-parity.py /tmp/copy  # or any checkout root
```

It exits 0 (printing `OK <doc> <version> <n>/<n>` per document), 1 on a named failure, 2 on a usage error, and checks:
the manifest contract; that each document's `vi` and `en` files exist as exactly `<prefix>.<version>.<lang>.md`
(the map is `tos -> terms`, `privacy -> privacy`) at the *same* version as the manifest (a `vi`≠`en` version fails),
with the legal directory, `manifest.json` and each document rejected before they are read if any path component under
the supplied root is a symlink or the real path escapes the root; one `#` title and no front matter; the `## N.` sets
are identical and exactly `1..10` / `1..12`; the required links inside their owning slot (privacy slot 3 = YouTube
Terms + Google Privacy Policy, slot 4 = the Google permissions page) plus the slot-3 "uses YouTube API Services" and
slot-7 never-ask-for-a-password statements, and the ToS slot-8 YouTube Terms link + III.A.1 binding sentence; the
per-slot block structure (paragraphs and top-level bullets) equal in both locales; the Markdown-subset allow-list
(only the `# ` title, `## N.`/`### ` headings, top-level `- `/`1. ` items, `> ` quotes, paragraph and blank lines —
a quote or list remainder is re-classified after its marker, so fenced/indented code, `####`+, Setext underlines,
`---`/`***` rules, tables, images, footnotes, reference-style definitions, raw HTML/autolinks, bare URLs, `*`/`+`
bullets and ordered markers above `1. ` fail) with each `](target)` parsed strictly (`https://` with a dotted host,
`mailto:` with a non-empty recipient before any `?`, or a `/`-rooted path only — unclosed, spaced, backslashed or
`//host` targets fail); no version literal or effective date in the prose (each read capped at 256 KiB); the `pc.v1`
citation; and no restated retention window in the terms.
It also asserts the bullet counts the acceptance checks name (privacy slot 5 = 6 bullets; terms slot 4 = 4 bullets),
so a translation that drops a bullet fails.

Expected output on this commit:

```
OK privacy 1.0.0 10/10
OK tos 1.0.0 12/12
```

The same checks as the short commands the task file names:

- Heading parity: `grep -E '^## [0-9]+\.' web/src/content/legal/privacy.1.0.0.vi.md | sed -E 's/^## ([0-9]+)\..*/\1/' | tr '\n' ' '` against the `.en.md` file — both print `1 2 3 4 5 6 7 8 9 10 `.
- Required links: `grep -c 'https://www.youtube.com/t/terms'`, `…/policies.google.com/privacy`, `…/security.google.com/settings/security/permissions` — each ≥ 1 in both locales.
- Subset rejection: `grep -nE '<[a-zA-Z/!?]|^\||^ {0,3}\[[^]]+\]:' web/src/content/legal/privacy.1.0.0.*.md` — no output.
- Manifest: `python3 -c "import json;d=json.load(open('web/src/content/legal/manifest.json'));print(sorted(d))"` — `['privacy', 'tos']`.
- `pc.v1` citation: `grep -c 'pc\.v1' web/src/content/legal/privacy.1.0.0.*.md` ≥ 1 in each locale.

## 6. Review steps (recorded, not automated)

1. Read the vi file start to finish, then the en file: a sentence present in one and missing in the other is a finding.
2. Open the two URLs in slot 3 and the URL in slot 4 (done 2026-09-29, see §2).
3. Read slot 5 against `plans/13-compliance.md` §5.4 (Channel / Account / Episode / Backups): every window and every kept row there must have a matching sentence here.
4. Confirm the ⚠️ rows of §1/§4 are still marked and that no ⚠️ item reads as settled in the policy text.
5. Re-run §5 and the manifest command; then hand the files to 10.3.1-T1 (pages) and 13.2.1-T1 (acceptance).

**Executed 2026-09-29 (this commit).** (1) Both locales were read end to end; the per-slot structure and the
number/URL multisets are identical, so no sentence exists in one locale only. The deliberate asymmetries are by design:
the Vietnamese files explain the platform wording that stays official in English ("Authorized Data" → "Authorized Data"
(dữ liệu được uỷ quyền), scope, credit, autopilot, veto, synthetic media, publisher of record). (2) The three URLs were
opened, results in §2. (3) Slot 5 was read against §5.4: all six windows and every kept row (Episode, Channel, Account,
Backups) have a matching sentence. (4) The ⚠️ rows are marked and no ⚠️ item reads as settled: the 18+ clause is stated
as a product rule, and the transfer paragraph relies on the providers' contractual safeguards. (5) `legal-parity.py`
exits 0 (`OK privacy 1.0.0 10/10`, `OK tos 1.0.0 12/12`) and the manifest command prints `['privacy', 'tos']`.

## 7. Deferred to a later version (recorded, not dropped)

| Item | Why |
|---|---|
| Lawyer review and the closure of the ⚠️ rows | 13.1.1-T2 (S) runs before the service has any user other than the owner (O1) |
| Cookie notice cross-reference | 13.1.4-T1 (S) owns the cookie inventory and its notice; this policy states no cookie claim, so no sentence has to change when it lands |
| Vietnamese-law duties (records of processing, impact-assessment dossier, breach notice) | 13.6.1-T1 (S) memo; until it lands, no PDPL-compliance claim appears in this text (13 README D16) |
