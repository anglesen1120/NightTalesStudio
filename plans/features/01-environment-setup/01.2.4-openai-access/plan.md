# 01.2.4 — OpenAI access with a hard budget (feature plan)

> Refines: `../../../01-environment-setup.md` story 01.2.4, §2 R2, §10 open question "budget hard vs alert". Contract: `../README.md` (facts §1, finding F11, decisions D3/D6/D7/D9, gates §7). Template: `../../../00-overview.md` §10.1.
> Legend: ✅ read from source/docs (README §1 id) · ⚠️ unverified — confirm in the dashboard/at run time · [DECISION] · [INFERENCE] · [OPEN].
> Status: detailed plan, 2026-09-23. Nothing created in the OpenAI dashboard yet.

## 1. Scope

| | |
|---|---|
| Story | **01.2.4 OpenAI access** — *As the owner I want API access with a hard budget so that a bug cannot overspend.* Must |
| Tasks | 01.2.4-T1 org verification + billing guard (XS) · 01.2.4-T2 projects, allow-lists, hard limits, keys → Keychain (XS) · 01.2.4-T3 access tests + key-leak/rotation procedure (XS) = **0.75 d** |
| In scope | OpenAI org settings used by the trial (verification, prepaid credit, auto-reload off, org hard limit); two projects `nts-trial-story` / `nts-trial-image` with model allow-lists and hard limits; one Restricted key per project; storing both keys in the macOS Keychain (D3); direct positive/negative API checks; recorded limits and rate-limit tier; key-leak/rotation procedure text for the runbook |
| Out of scope | MPT config template and rendering (01.2.2-T2, D5 — consumes `nts.openai.story` / `nts.openai.image`); usage tap (01.3.1-T2, D6); image quality decision (CR-3, plan 03); production org/projects, auto-reload with monthly limit and funding view (DECISIONS §10.5, plan 12); Admin API keys (O7) |
| Acceptance (story, restated) | Given the two trial projects with hard limits, When a key is used, Then it can call only its project's allow-listed model, spend stops at the project/org hard limit (with a documented delay), and the key exists only in the Keychain and in the rendered 0600 `config.toml` on the FileVault disk (D3) |

The story's original wording "stored only in local `config.toml`/env" is superseded by D3 (Keychain; `config.toml` rendered at start). Because of F05 there is no secret on the USB project volume.

## 2. Confirmed facts

| # | Fact used here | Status / source |
|---|---|---|
| O1 | Spend limits per org and per project; each is a **Spend alert** (notify only) or a **hard spend limit** (toggle **Enforce a hard limit**). Over the limit, requests get `429` with `project_spend_limit_exceeded` / `organization_spend_limit_exceeded`. **Enforcement is not instant**, so spend can go slightly over. Limits reset monthly. Project path: Project settings → Limits → Spend → Edit spend limit → Monthly spend limit. Org path: `https://platform.openai.com/settings/organization/limits` → Spend. Separate tier-based approved usage limit → `organization_usage_limit_exceeded` | ✅ https://developers.openai.com/api/docs/guides/spend-limits (the older "does not enforce a hard cap" paragraph in help 9186755 is outdated; the guide is authoritative) |
| O2 | Prepaid credits (minimum $5, default $10, expire after 1 year, non-refundable except listed cases). **Auto-reload is on by default** and can be turned off. At zero balance → `credit_balance_exhausted`, but access may not stop at once (negative balance deducted from the next purchase). "Do not rely on the prepaid balance as an instantaneous spending cutoff." The monthly reload limit caps purchases, not usage | ✅ https://help.openai.com/en/articles/8264644-setting-up-and-managing-prepaid-api-billing |
| O3 | Per-project model allow-list: org settings → project → **Limits → Model Usage**; **Owner only**. Admin API equivalent `model_permissions` `allow_list`/`deny_list` (needs an Admin key — not used) | ✅ https://help.openai.com/en/articles/9186755-managing-projects-in-the-api-platform, https://developers.openai.com/api/docs/guides/admin-apis |
| O4 | Key permissions **All** (default) / **Restricted** / **Read only**. Chat completions and images need **Model capabilities: Request** (`model.request`); **List models: Read** recommended. One permission covers chat **and** images, so key scope alone cannot separate the two; the allow-list (O3) does | ✅ https://developers.openai.com/api/docs/guides/rbac, https://help.openai.com/en/articles/8867743-assign-api-key-permissions |
| O5 | "you **may** need to complete the API Organization Verification … before using GPT Image models". Status shown under Settings → Organization → General | ⚠️ https://developers.openai.com/api/docs/guides/image-generation, https://help.openai.com/en/articles/10910291-api-organization-verification. Plan as if required; external wait of unknown length |
| O6 | Images: `quality` default `auto` when omitted; flare accepts `low/medium/high/xhigh/max`; the response echoes the resolved quality. `moderation` `auto` (default) / `low`. `usage` = `input_tokens`, `input_tokens_details{text_tokens,image_tokens}`, `output_tokens`, `total_tokens`, optional `output_tokens_details`; the reference still says "gpt-image-1 only" → may be absent, handle it. Set `model` explicitly (fallback `dall-e-2`) | ✅ https://developers.openai.com/api/reference/resources/images/methods/generate |
| O7 | Usage/Costs APIs need an **Admin key**; Costs has only 1-day buckets | ✅ admin usage/costs references → [DECISION D6] no Admin key in M0; per-run usage comes from the tap |
| O8 | `gpt-image-2.5-flare` (superseded by `gpt-image-2`, 01 DEC-11 — official-API token prices kept as reference; relay price for `gpt-image-2` resolved ✅ **184 VND/image** flat, owner 2026-09-28, 02 CR-27; relay rate re-check [OPEN]): text in $5, image in $8, image out $30 per 1M tokens; **Tier 1 = 100k TPM / 5 images per minute**. `gpt-6-luna`: $0.10 in / $0.50 out per 1M | ✅ https://developers.openai.com/api/docs/pricing, https://developers.openai.com/api/docs/models/gpt-image-2.5-flare |
| M1 | `openai_*` and `openai_image_*` keys live in `[app]` | ✅ MPT `config.example.toml` |
| M4 | `openai_image` sends only `{model, prompt, n: 1, size}` (no `quality`); retries 429/5xx 3× (5/15/30 s); on any failure it logs `openai image generation failed` and **skips the keyword** (no error, no fallback) | ✅ `app/services/material.py` |
| M5 | LLM `openai`: `chat.completions.create`, not streaming; empty `openai_base_url` → api.openai.com; empty model → `gpt-5.5`; retries 5× | ✅ `app/services/llm.py` |
| DEC §2 | Keys only local, never committed or pasted into chat; spending limit set in the dashboard | ✅ `DECISIONS.md` §2 |
| DEC §4 | Relay price for `gpt-image-2`: **184 VND/image** flat (owner 2026-09-28, 02 CR-27; ≈ $0.0072 at SBV 25,641 VND/USD, `trial/prices.json` `unit: per_image`; CRV-0928-5). Official-API `gpt-image-2.5-flare` reference (superseded, 01 DEC-11): 1024×1024 low ~$0.006 · medium ~$0.013 · high ~$0.053 (floor prices) | ✅ `DECISIONS.md` §4 |
| DEC §10.11 | Node image key lives in a **separate OpenAI project** (image models only, own hard budget) | ✅ `DECISIONS.md` §10.11 → the trial uses the same split |

Questions closed: 01 §10 "budget hard vs alert" → a hard limit exists (O1), but it is not instant. Still open (⚠️, answered by T1/T3): whether verification is needed for this org (O5); the exact status/code of an allow-list denial; whether `usage` is present for flare (O6); exact dashboard labels (UI may differ from the docs).

## 3. Domain and data

No database. Resources and where they live:

| Resource | Name / value | Where | Owner of change |
|---|---|---|---|
| Org billing | prepaid; credit purchase amount = owner decision (proposal ≥ D7 org limit $30, [INFERENCE] $30 covers the full trial with headroom); **auto-reload off** | OpenAI dashboard | OpenAI org Owner |
| Org spend limit | $30/month, **Enforce a hard limit** on | dashboard | Owner |
| Project | `nts-trial-story`: allow-list `gpt-6-luna` only (+ `gpt-6-sol` temporarily during 02.1.3-T1 only, 02 CR-17), hard limit $5/month | dashboard | Owner (O3) |
| Project | `nts-trial-image`: allow-list `gpt-image-2` only (supersedes `gpt-image-2.5-flare`, 01 DEC-11), hard limit $25/month | dashboard | Owner |
| Key | `nts-trial-story-mac-<YYYYMM>` (e.g. `nts-trial-story-mac-202609`), Restricted: Model capabilities = Request, List models = Read, everything else None | project `nts-trial-story`; value in Keychain service `nts.openai.story` | Owner creates; typed into the Mac prompt |
| Key | `nts-trial-image-mac-<YYYYMM>`, same permissions | project `nts-trial-image`; Keychain `nts.openai.image` | same |
| Rendered config | `[app] openai_api_key = "${NTS_OPENAI_STORY_KEY}"`, `openai_image_api_keys = ["${NTS_OPENAI_IMAGE_KEY}"]` (M1, M4, M5) | `worker-node/mpt/config.template.toml` → `$NTS_HOME/src/mpt/config.toml` 0600 | 01.2.2-T2 (D5); this story supplies the two Keychain entries |
| Access record | `trial/baselines/openai-access-mac-<YYYYMMDD>.json` (tracked, secret-free) | repo | T3 |

Invariants:
- Story traffic (`chat.completions`, M5) is billed only to `nts-trial-story`, image traffic (`images/generations`, M4 — translated to `/responses` when `OPENAI_IMAGE_API=responses`, host `OPENAI_IMAGE_HOST_MODEL` on the image key; 01 DEC-11) only to `nts-trial-image`. A key can reach only its project's allow-listed model (O3). Key scope is identical for both, so the allow-list is the separating control (O4).
- Project limits $5 + $25 ≤ org limit $30 (D7). The org limit is the outer bound if a project limit is misconfigured.
- Auto-reload off → the prepaid balance is a second (delayed) ceiling (O2).
- No key value ever appears in the repo, argv, shell history, logs, `run.json`, the access record, chat or screenshots (D3, DECISIONS §2).

Access-record schema (`openai-access-mac-<YYYYMMDD>.json`, written by `openai-access-check.sh`, reviewed by hand):

```json
{
  "schema": 1,
  "checked_at": "2026-09-24T09:15:00Z",
  "org": {"verification": "verified|pending|not_required", "auto_reload": false,
          "hard_limit_usd": 30, "spend_alert_50pct_usd": 15, "credit_purchased_usd": "<owner>", "usage_tier": "tier-1",
          "image_rate_limit_ipm": 5},
  "projects": {
    "nts-trial-story": {"allow_list": ["gpt-6-luna"], "hard_limit_usd": 5, "spend_alert_50pct_usd": 2.5, "key_name": "nts-trial-story-mac-202609", "key_permission": "restricted"},
    "nts-trial-image": {"allow_list": ["gpt-image-2"], "hard_limit_usd": 25, "spend_alert_50pct_usd": 12.5, "key_name": "nts-trial-image-mac-202609", "key_permission": "restricted"}
  },
  "checks": [
    {"id": "C1", "key": "story", "endpoint": "/v1/chat/completions", "model": "gpt-6-luna",
     "http_status": 200, "error_code": null, "usage": {"prompt_tokens": 0, "completion_tokens": 0}, "pass": true}
  ]
}
```

`org` and `projects` values are typed from the dashboard by the operator (there is no Admin key to read them, O7). `checks[]` is filled by the script. Field names inside `usage` are copied as the API returns them.

## 4. Use cases (operator procedures)

| # | Procedure | Actor | Command / place | Result |
|---|---|---|---|---|
| U1 | Check/submit Organization Verification | Owner | dashboard Settings → Organization → General ⚠️ label | status recorded; if pending, T3 image checks and 01.2.2-T6 wait (T1) |
| U2 | Buy prepaid credit; turn auto-reload **off** | Owner | Settings → Billing ⚠️ label | balance > 0, auto-reload off (T1) |
| U3 | Set org hard limit $30 | Owner | `https://platform.openai.com/settings/organization/limits` → Spend → Edit spend limit, **Enforce a hard limit** | O1 (T1) |
| U4 | Read usage tier + image rate limit | Owner/dev | Limits page ⚠️ | `usage_tier`, `image_rate_limit_ipm` in the access record (T1) |
| U5 | Create projects, allow-lists, project hard limits | Owner | Projects → Create; Limits → Model Usage; Limits → Spend (O1, O3) | two projects per §3 (T2) |
| U6 | Create Restricted keys and store them | Owner at the Mac | Project → API keys ⚠️ → Create (Restricted); then `worker-node/bin/secrets.sh set nts.openai.story` / `… set nts.openai.image` (interactive prompt, D3) | Keychain entries exist; key shown once ⚠️ and never copied elsewhere (T2) |
| U7 | Verify access | Developer | `worker-node/bin/openai-access-check.sh` | C0s–C7 pass; C8 read back; access record written (T3) |
| U8 | Rotate (M0 close, or suspected leak) | Owner + dev | runbook section "OpenAI key leak / rotation" (text in T3) | old key revoked, new key stored, U7 green |
| U9 | Revoke at M0 close | Owner | Project → API keys → Revoke; optionally archive projects ⚠️ | gate 01-closed item "keys rotated or revoked" (§11) |

Rule: **the developer may not be an org Owner** (O3 needs Owner). U1–U6 and U8/U9 dashboard parts are owner steps. The key value goes from the dashboard straight into the `secrets.sh` prompt on the Mac, typed or pasted by the owner at the Mac. It is never sent by chat, e-mail or a shared doc.

## 5. API and events

External calls only (no own API). All go direct to the pinned `OPENAI_BASE_URL` in T3 (relay, 01 DEC-9/CR-15); after 01.3.1-T2, MPT goes through the tap (D6), which uses the same endpoints.

| Call | Used by | Body (T3) | Expected |
|---|---|---|---|
| `GET /v1/models` | T3 C0s / C0i (story / image key) | — | 200 (List models = Read, O4) |
| `POST /v1/chat/completions` | MPT story (M5); T3 C1/C4/C7 | `{"model":"gpt-6-luna","messages":[{"role":"user","content":"Reply with OK."}],"max_completion_tokens":16}` | story key: 200 + `usage` (C1); story key with a non-allowed model `gpt-6-astra`: denied (C7; 02 CR-17); image key: denied (C4) |
| `POST /v1/images/generations` (translated to `/responses` + `image_generation` tool when `OPENAI_IMAGE_API=responses`, 01 DEC-11/CR-16) | MPT images (M4); T3 C2/C3 | `{"model":"gpt-image-2","prompt":"A foggy empty village road at dusk, illustration","n":1,"size":"1024x1024","quality":"low"}` | image key: 200, `quality` echoed `low`, `usage` (may be absent, O6) (C3); story key: denied (C2) |
| `GET /v1/files` | T3 C6 | — | denied: proves the key is Restricted, not All (O4) |

Error codes the trial must recognise (logged by the tap in `error_code`, D6):

| HTTP | `error.code` | Meaning | MPT behaviour |
|---|---|---|---|
| 429 | `project_spend_limit_exceeded` / `organization_spend_limit_exceeded` | hard limit hit (O1) | images: 3 retries then **keyword skipped** (M4); story: 5 retries (M5), task outcome ⚠️ |
| 429 | `organization_usage_limit_exceeded` | tier monthly limit (O1) | same |
| 429 | `rate_limit_exceeded` ⚠️ (code name not in facts) | Tier 1 5 images/min (O8) | same: retry at 5/15/30 s, then skip |
| 4xx | `credit_balance_exhausted` (status ⚠️) | prepaid balance 0 (O2) | same |
| 400 | `moderation_blocked` | moderation (DECISIONS §5.2) | skip |
| 403/404 ⚠️ | ⚠️ record | model not in allow-list / verification missing | skip (images) / fail (story ⚠️) |

## 6. UI

Operator UI = OpenAI dashboard only. Labels are quoted from the docs of O1–O5. **All labels are ⚠️: the UI may differ from the docs.** Record the actual label in the task file notes when it differs.

| Page | Label (docs) | Used in |
|---|---|---|
| Org settings → General | Organization Verification (O5) | T1 |
| Billing | Prepaid credits, Auto recharge / auto-reload (O2) | T1 |
| `settings/organization/limits` | Spend → Edit spend limit → Monthly spend limit → Enforce a hard limit (O1); rate limits by tier (O8) | T1 |
| Project → Limits | Model Usage (O3), Spend (O1) | T2 |
| Project → API keys | Create new secret key → Permissions All / Restricted / Read only → Model capabilities: Request, List models: Read (O4) | T2, T3, U8/U9 |

## 7. Jobs and integrations

- **External wait:** Organization Verification (O5) has an unknown duration ⚠️ → T1 runs on **day 1** (README §5). It blocks the T3 image check (C3), 01.3.1-T2 end-to-end tap check, 01.2.2-T6 wiring render and gate 01-ready. It does not block T2 or the story checks.
- **Rate limit vs MPT retry-then-skip (F11):** Tier 1 = 5 images/min (O8). MPT retries a 429 at +5, +15, +30 s (≈ 50 s) and then **silently drops the keyword** (M4). Whether MPT issues image calls sequentially or in parallel is ⚠️ (01.2.2-T6 observes it). [INFERENCE] Sequential calls with multi-second latency stay near or below 5/min, but a burst plus retries can drop images. Consequences:
  - The collector counts skipped keywords (`openai image generation failed` log lines vs the number of `video_terms`) (01.3.1-T3). A run with skips is recorded, not hidden.
  - A spend-limit 429 has the same effect as a rate-limit 429. A run that hits the $25 image limit produces a video with missing images and no error. The tap's `error_code` distinguishes them.
  - A tier upgrade is not requested in M0 (the numbers should reflect Tier 1). Record the tier in the access record and in `CARRY-OVER.md` (01.3.3). This is F24: the access record and the T1 org fields (`usage_tier`, `image_rate_limit_ipm`) are the evidence that closes it; 01.3.1-T1's `image_http_429_count`/`image_failed_count` columns and 01.2.2-T6 consume them.
- **Budget sizing [INFERENCE]:** one 5-min video ≈ 60 images (DECISIONS §4). Quality is `auto` (F03), and the size is 1536×1024 or 1024×1536 (DECISIONS §4 note), so the per-image cost is unknown. At a pessimistic ~$0.08/image, a run ≈ $5 → $25 covers the trial (2 rows) plus calibration and retries. Story ≈ cents per run at luna prices (O8), so $5 is ample. If 01.3.1-T4 calibration shows > $8 per run, the owner raises the image limit before 05.5.2 (a decision, not an auto-change).
- **Limit delay:** hard limits are not instant (O1) and the prepaid balance is not an instant cutoff (O2). Expect a small overrun and never plan a run that needs the last dollars of a limit.
- **Auto-reload deviation [DECISION, trial-scoped]:** DECISIONS.md §258 says production/staging should "enable auto-reload with a monthly limit" (runway continuity for paying users). T1 instead turns auto-reload **off** for the two M0 trial projects (O2, D7): there is no multi-day runway need for a 2-day trial, and a hard, non-reloading cap is the whole point of F11/F24's "a bug cannot overspend" goal. This is a deliberate divergence, not an oversight — do not "fix" it back to auto-reload-on for the trial. Production projects follow §258 as written (out of scope here).
- **Daily usage check [DECISION, mandatory, security review].** During the trial window (T1 through gate 01-closed) the owner checks each project's usage page **once per calendar day**, not only after a run: OpenAI's usage/cost totals are daily-bucketed (O7) and can lag a run by up to a day, so a same-day-only check can miss overnight or delayed spend. Record any unexplained delta in `trial/runs/<run_id>/notes.md` or, if no run is active, in a dated line of `worker-node/CARRY-OVER.md` draft notes.
- **Integration with other tasks:** 01.1.2-T2 `secrets.sh` (Keychain helper) → T2; 01.2.2-T2 renderer reads `nts.openai.story`/`nts.openai.image` into `NTS_OPENAI_STORY_KEY`/`NTS_OPENAI_IMAGE_KEY` (D5); 01.3.1-T2 tap passes `Authorization` through (D6); 01.3.1-T1 `trial/prices.json` uses O8 prices.

## 8. Security and privacy

| Threat | Control |
|---|---|
| Key leaks from the Mac (repo, USB volume, history, logs) | Keychain only (D3). Entered through the `secrets.sh` interactive prompt (no argv). Scripts read with `security find-generic-password … -w` inside a function, pass it to curl through `-H @<(printf 'Authorization: Bearer %s\n' "$KEY")` (process substitution; `printf` is a builtin, so the key is not in any process argv), and never use `set -x`. The only file with the key is the 0600 `config.toml` on the FileVault disk (D1, D5). gitleaks pre-commit hook (01.1.2-T4) |
| Key entry during a screen-share (T2 step 6/§SEC-11 pt.2, mandatory) | The owner's clipboard **and clipboard-manager history** (e.g. Maccy/Paste/system Clipboard History) are cleared right after pasting the key into the `secrets.sh` prompt (`pbcopy </dev/null`, then clear the manager's history entry for that item). The key value is **never** displayed with `security … -w` to "double check" it — verification uses only the length/distinctness check (T2 step 9), never the value itself |
| Leaked key used to overspend | **Blast radius per key** = its project's allow-listed model up to the project hard limit ($5 story / $25 image), bounded by the org limit $30 and the non-auto-reloading prepaid balance (O1, O2). Enforcement delay → small overrun possible. **Mandatory** spend alerts at 50 % of each hard limit (org $15, story $2.5, image $12.5 — security review) give the owner a same-day warning before the hard stop |
| Leaked key used for other APIs (files, fine-tuning, assistants, batch …) | Restricted key: only Model capabilities = Request + List models = Read (O4); checked by C6 |
| Story key used for images (or the reverse) to escape a budget | Allow-list per project (O3); checked by C2, C4 |
| Silent model upgrade (e.g. empty `openai_model_name` → `gpt-5.5`, M5; or `gpt-6-astra`; `gpt-6-sol` only while temporarily allowed for 02.1.3-T1) | Allow-list rejects it (C7). D5 guard rejects an empty model name |
| Admin key compromise (org-wide read/write) | **No Admin key is created in M0** [DECISION, D6]. Usage per run comes from the tap. Dashboard totals are read by the owner by hand. O7 would require one → avoided |
| Key shown in screenshots/chat during setup | Owner types or pastes the key only into the Mac prompt. No screenshots of the key page after creation. DECISIONS §2 |
| `config.toml` or `$NTS_HOME` copied, synced, or shown (security review SEC-13) | Treated as a **full leak**, not a partial one: the runbook (T3 §D) now rotates every Keychain-held key on this trigger, not just the two OpenAI keys, because `config.toml` also carries `nts.mpt.api_key`, `VIENEU_API_KEY` and the proxy key (D5) |
| Stale keys after M0 | Keys named `…-mac-<YYYYMM>` and **revoked at M0 close** (D7, gate 01-closed). Production uses new projects and keys (DECISIONS §10.5, §10.11). M0 close also deletes the two rendered config files (`config.toml`, `mpt-config.last.redacted.toml`) so no plaintext key material survives on the Mac |
| Data sent to OpenAI | T3 sends only fixed non-personal prompts. Trial prompts are fiction/scenery (DECISIONS §8 item 2) |

Retention: the access record keeps no key, prompt text beyond the fixed test strings, or image bytes. The test PNG stays under `$NTS_HOME/runs/access-check-*/` (never tracked) and may be deleted.

## 9. Test plan

Runnable by `worker-node/bin/openai-access-check.sh` (T3) except the dashboard observations (manual, recorded in the access record).

| Id | Check | Expected | Cost |
|---|---|---|---|
| C0s / C0i | story / image key `GET /v1/models` | 200 | 0 |
| C1 | story key → chat `gpt-6-luna`, `max_completion_tokens: 16` | 200, `usage.prompt_tokens`/`completion_tokens` present | < $0.0001 |
| C2 | story key → images `gpt-image-2` (flare superseded, 01 DEC-11) | non-2xx; record status + `error.code` ⚠️ | 0 |
| C3 | image key → images 1024×1024 `quality: low` | 200; `quality` echoed `low`; `usage` recorded or `n/a:absent` (O6) | 184 VND ≈ $0.0072 on the relay (02 CR-27, CRV-0928-5; $0.006 was the official-API flare floor, DEC §4) |
| C4 | image key → chat `gpt-6-luna` | non-2xx; record | 0 |
| C5 | image key → images with `quality` omitted | **not run** in T3 (default `auto` = unknown cost; measured in 01.2.2-T6 via the tap) | — |
| C6 | story key → `GET /v1/files` | non-2xx (insufficient permission); record | 0 |
| C7 | story key → chat `gpt-6-astra` | non-2xx; record (02 CR-17) | 0 |
| C8 (manual) | dashboard: auto-reload off, org hard limit $30 enforced, project limits $5/$25 enforced, allow-lists, tier, verification | as §3 | — |

Not tested (and why): actually hitting a hard limit (costs money, enforcement delayed; O1 is trusted and the tap logs the code if it happens); rate-limit behaviour (observed in 01.2.2-T6 / the trial through the tap and the skipped-keyword count).

## 10. Tasks and re-estimate

| Task | Title | Size | Depends on | Blocks |
|---|---|---|---|---|
| 01.2.4-T1 | Org verification + billing guard | XS 0.25 | — | T2, T3 (image checks), 01.2.2-T6 |
| 01.2.4-T2 | Projects, allow-lists, hard limits, keys → Keychain | XS 0.25 | T1 (billing done), 01.1.2-T2 (`secrets.sh`) | T3, 01.2.2-T2 render with real keys, 01.3.1-T2 |
| 01.2.4-T3 | Access tests + key-leak/rotation procedure | XS 0.25 | T2, 01.1.1-T1 (`uv`, `curl`) | 01.3.1-T2, 01.2.2-T6, gate 01-ready |
| **Total** | | **0.75 d** | | |

High-level 01 §6 had 01.2.4-T1 XS (0.25) → detailed 0.75 (+0.5), matching README §9. The reason is F11: two projects, allow-lists, negative tests. Size note: the sizes count developer effort only. The verification wait (O5) is calendar time outside the estimate, and owner time in the dashboard (~30 min [INFERENCE]) is not a developer day. T3 is tight at XS because it includes writing the check script and the runbook section. It stays XS because the script is ~80 lines of bash/curl with no new logic; flag it if it slips.

New files introduced by this feature (fit D1/D9):

| File | Tracked | Created in |
|---|---|---|
| `worker-node/bin/openai-access-check.sh` | yes (secret-free) | T3 |
| `trial/baselines/openai-access-mac-<YYYYMMDD>.json` | yes (secret-free) | T3 |
| `$NTS_HOME/runs/access-check-<YYYYmmdd-HHMMSS>/` (raw responses minus image bytes, test PNG) | no | T3 |
| `worker-node/README.md` section "OpenAI key leak / rotation" | yes | text specified in T3; the file is owned by 01.1.1-T3 (request to BaseToolingPlan) |

## 11. Rollout

1. **Day 1:** T1 (verification submitted first, then billing and org limit).
2. **Day 2:** T2 (projects, keys → Keychain), then T3 (story checks immediately; image checks as soon as verification passes).
3. Keys are consumed by the D5 renderer at the next MPT start (01.2.2-T4). The tap (01.3.1-T2) is verified end-to-end with the same keys.
4. **Monitoring during M0 [mandatory, daily]:** the owner checks each project's usage page **once per calendar day** during the trial window, not only after a run (OpenAI usage/cost totals are daily-bucketed and can lag, O7). Per-run numbers come from `trial/runs/<run_id>/openai_usage.jsonl` (D6). Any 429 `*_spend_limit_exceeded` in the tap log, or a 50 % spend-alert e-mail, stops further runs until the owner decides.
5. **Rotation (on suspicion of a leak, or `config.toml`/`$NTS_HOME` exposure, security review SEC-13):** runbook procedure (T3 §D) — revoke first, then replace, **all** Keychain-held keys, not only the OpenAI ones.
6. **02.1.3 A/B window (02 CR-17):** owner adds `gpt-6-sol` to the `nts-trial-story` allow-list right before 02.1.3-T1, removes it right after 02.1.3-T2, and records both dates in the access record; the $5 hard limit is unchanged.
7. **M0 close (gate 01-closed):**
   1. Owner: Project `nts-trial-story` → API keys → **Revoke** `nts-trial-story-mac-<YYYYMM>`; same for `nts-trial-image` ⚠️ labels.
   2. Developer: `security delete-generic-password -a "$USER" -s nts.openai.story` and `-s nts.openai.image` (exit 0), then `worker-node/bin/secrets.sh check` → reports both as missing (lib.sh `secret` would exit 2).
   3. Developer: re-render is no longer possible (renderer fails on a missing variable, D5) → MPT on the Mac stays down until new keys exist. That is intended.
   4. Developer: `rm -f "$NTS_HOME/src/mpt/config.toml" "$NTS_HOME/mpt-config.last.redacted.toml"` (security review: no plaintext key material may survive the trial on the Mac). Confirm: `test ! -e "$NTS_HOME/src/mpt/config.toml" && echo removed`.
   5. Owner: keep the projects (history) or archive them ⚠️. Record the final spend per project and the org in `CARRY-OVER.md` (01.3.3) as the Mac baseline.
   6. If the Mac is kept for development after M0, new keys `…-mac-<new YYYYMM>` are created by repeating T2 + T3 instead of reusing the old ones.
7. **Rollback:** everything is dashboard state plus two Keychain items. Revoking keys and setting project limits to $0 ⚠️ (or archiving) removes access completely. No repo state depends on the OpenAI org except the secret-free access record.
