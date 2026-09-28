# `infra/scoring` — hosting evaluation for plan 11

The scoring harness behind **11.2.1-T1**. It scores 2–3 providers against the
hosting checklist of `plans/11-worker-deployment.md` §5.6 (as amended by 11 CR-15
/ `plans/features/11-worker-deployment/README.md` 11 §10 T74), and it keeps the
evidence as immutable, dated capture files so that a later price change is a new
capture plus a re-run — never an edit of an old one.

The folder lives here until 11.2.2-T1 creates `infra/modules/`; it is a decision
artefact, not infrastructure code, and it needs no cloud credentials.

## 1. Files

| File | What it is |
|---|---|
| `criteria.json` | the machine-readable checklist: hard gates, scored criteria with weights, and the model parameters (duty cycle, days per month, VRAM floor) |
| `score.py` | stdlib-only scorer; reads `criteria.json` + all captures, applies the hard gates, normalises each criterion across providers, multiplies by the weight, prints the ranked table and a per-criterion diff |
| `captures/<provider>-<yyyy-mm-dd>.json` | one immutable capture per provider: per criterion `value`, `unit`, `source_url`, `captured_on`, `captured_by`, `quote` |
| `latency.md` | the latency measurement method and the outstanding commands (⚠️ until a region is rented) |
| `README.md` | this page |

## 2. The §5.6 checklist this file encodes

Copied verbatim from `plans/11-worker-deployment.md` §5.6:

> GPU types available and their VRAM (VieNeu + whisper on one card); hourly and
> monthly price (official page, dated); billing granularity (per second/hour);
> ability to stop/start GPU nodes; region and latency to Vietnam and to
> OpenAI/YouTube; managed PostgreSQL with PITR; block volumes attachable to a VM
> with encryption at rest, snapshots and a published GB-month price, offered in
> two availability zones (or two locations) for `storage-backup-<env>` — no
> object-storage product is scored (11 CR-15, T74); a published internet egress
> price for `media.<domain>` downloads (11 CR-15, T74); private networking
> between services; data-processing terms/GDPR/VN data rules ⚠️; support
> quality; account/payment options for a Vietnam-based business.

### 2.1 Hard gates (a provider failing any one is not scored further)

`criteria.json.hard_gates` — the T74 set, each with the §5.6 heading it comes
from:

1. `gpu_vram_gb` — one GPU per node, ≥ 16 GB VRAM (24 GB preferred, F28/D9).
2. `pg17_pitr` — managed PostgreSQL 17 with base backup + continuous WAL archive (E10).
3. `block_volume_gates` — block volumes attachable to a VM with encryption at rest,
   snapshots and a published GB-month price (T74, 11 CR-15).
4. `block_volume_two_zones` — the same volume type offered in two availability
   zones (or locations) for `storage-backup-<env>` (T74).
5. `media_egress_price` — a published internet egress price for `media.<domain>` (T74).
6. `private_networking` — private networking between services.
7. `secret_manager` — a provider-native secret manager (D15).
8. `container_registry` — a container registry (D15/D16).
9. `telemetry_non_agpl` — a telemetry backend option that is not self-hosted AGPL
   (E12, F16, O6).
10. `owner_locked_eligible` — the provider is **not** excluded by a locked owner
    decision. `DECISIONS.md` §3 and 11 README D21 (owner O7, 2026-09-27) lock the
    storage engine and state that no AWS account is used, so **AWS is scored but
    never eligible**. This is a gate, not prose: the harness marks it in the
    eligible column.

A gate whose capture value is `null` or missing is **unknown**, not passed: the
provider is ineligible, the table prints `<gate> (unknown)` and `score.py` exits 1
if an unknown gate belongs to the top-ranked provider. That is what keeps a
"we could not find the evidence" from masquerading as a pass.

**No object-storage product is scored and no managed-storage gate exists.** D21
rules SeaweedFS self-hosted in every environment, and `weed filer.backup`
replaces H9's replication path, so the storage decision here is a block-volume
decision plus the `media.<domain>` egress price. `score.py` refuses to run if
`criteria.json` mentions an object-storage product at all (`BANNED_TERMS`).

### 2.2 Scored criteria and why each weight is what it is

Weights sum to exactly 1.0 (checked by `score.py --check-criteria`).

| id | weight | direction | why this weight |
|---|---|---|---|
| `gpu_price_per_hour` | 0.20 | lower | the largest unit cost that recurs every render hour; the duty cycle multiplies it directly |
| `monthly_at_duty_cycle` | 0.09 | lower | the same cost at the expected duty cycle (params: 6 h/day) — kept separate so a cheap hourly rate with an expensive minimum still shows |
| `internet_egress_gb` | 0.11 | lower | `media.<domain>` downloads grow with the audience; the only cost line that scales with success (T74) |
| `latency_to_vn_ms` | 0.08 | lower | operator/upload experience from Vietnam and the API-facing behaviour of every request |
| `latency_to_apis_ms` | 0.06 | lower | every render calls OpenAI and the YouTube Data API from the node |
| `managed_postgres_month` | 0.08 | lower | the only always-on managed service besides the registry |
| `block_volume_gb_month` | 0.07 | lower | two storage VMs, each with its own disk (50 GB staging / 200 GB prod, D21) |
| `volume_snapshot_gb_month` | 0.04 | lower | daily snapshots with 7-day retention on the primary volume only (T87) |
| `gpu_billing_granularity` | 0.04 | higher | decides how much of a short render burst is billed; matters only at a low duty cycle |
| `gpu_stop_start` | 0.04 | higher | the mechanism that makes a low duty cycle possible at all |
| `container_registry_price` | 0.03 | lower | a few GiB of images, but part of the D16 release path |
| `private_ca_option` | 0.03 | higher | T81: a provider private CA (else the owner-generated offline root) for `storage-<env>.internal` |
| `fqdn_egress` | 0.05 | higher | F14: with FQDN rules the egress design can name hosts instead of frozen IPs; without them 11.3.2-T1 keeps the IP/port allow-list |
| `vpn_bastion` | 0.02 | higher | the operator path to the nodes (O5) |
| `telemetry_option` | 0.02 | higher | D17/O6 backend, consumed by 11.6.2-T1 |
| `data_terms_region` | 0.02 | higher | flagged ⚠️ in §5.6; low weight because every finalist publishes a DPA and none of them sets the Vietnam question |
| `vn_payment_options` | 0.02 | higher | §5.6's account/payment item: can a Vietnam-based business pay for the account at all |

`direction` + the value matrix decide the normalisation: `lower` = cheapest gets
1.0, `higher` = true/best gets 1.0, an enum uses its declared `scale`, and a
criterion with no numeric value for **any** provider is neutral (1.0) for all of
them and is called out in the record.

### 2.3 Null policy and the noise band (both declared, both in the script)

Every criterion declares `null_policy` in `criteria.json`:

| policy | effect of a `null` value |
|---|---|
| `neutral` | 1.0 for that provider **even when other providers have numbers** — used only for the two latency items, which the task defers as ⚠️. Charging them 0.0 would bake "not measured yet" into the ranking as a penalty. |
| `fail` | 0.0 when at least one provider has a number; when *every* provider is null the criterion is neutral (1.0) for all and the report says so. A measured provider with no spread is equal-best, so a `null` beside a single number still scores 0.0. |

A boolean gate that is `null` is unknown (above); a boolean *criterion* that is
`null` follows its policy (so an unanswered `vn_payment_options` scores 0.0 beside
a `true`, never better).

`params.noise_tolerance` (0.01) groups providers whose totals are within that band
of the best total. Inside a band the order is decided by the declared tie-break —
**latency to the APIs → GPU price → internet egress → provider name** — applied by
`score.py`, never by prose in the record. A dimension is **skipped for the whole
band** as soon as any member has no number for it, so an unmeasured value neither
decides the order nor is scored as if it were infinitely bad; the report prints
each band's usable tie-break keys. The report also prints the band number of every
provider, so a reader can see whether the margin was decisive or inside the band.

## 3. Capture rules

- One file per provider: `captures/<provider>-<yyyy-mm-dd>.json`.
- Every entry needs `value`, `unit`, `source_url`, `captured_on`, `captured_by`
  and `quote`; `value`/`unit` may be `null` (unpublished) but `source_url`,
  `captured_on` and `quote` may never be empty — `score.py` treats such a capture
  as invalid and exits 1.
- The entry's `captured_on` must equal the file's `captured_on`.
- **Exactly one entry per id, no more and no less**: the value keys must equal the
  hard-gate ids plus the criterion ids of `criteria.json`. An unknown key is an
  error (it would look like evidence that scores nothing) and a missing key is an
  error (a silently absent gate could otherwise read as a pass).
- `unit` must equal the unit `criteria.json` declares for that id, and the value
  type must match: a boolean entry takes `true`/`false`/`null`, a numeric entry a
  finite number or `null`, an enum entry one of its declared `scale` keys or `null`.
- `monthly_at_duty_cycle` is **derived**: its value must equal
  `gpu_price_per_hour x params.duty_cycle_hours_per_day x params.days_per_month`,
  checked to 1e-6 relative, so a hand-typed monthly figure is an error.
- Captures are immutable. A page that changed after the capture yields a **new**
  capture with a new date, never an edit.
- A price that exists only behind a quote request is recorded as
  `"value": null, "note": "quote required"`; never estimated. A provider whose
  hard gate is quote-only is dropped and the record says why.
- Prices come from official pages/APIs only: AWS's price-list feed
  (`pricing.us-east-1.amazonaws.com` and the `b0.p.awsstatic.com`
  `meteredUnitMaps` feed behind the pricing pages), Azure's public Retail Prices
  API, and Google Cloud's pricing pages. No forum, reseller or blog number.
- No secret is needed anywhere; the harness takes paths as arguments only.
- **Input is treated as hostile**: every capture argument must be a regular file
  whose real path is a direct child of the captures directory (no symlinks, no
  `..`, no subdirectories), at most 32 files and 4 MiB in total, with JSON nesting
  capped at 64 levels; `NaN`/`Infinity`/`-Infinity` and over-deep or unbalanced
  JSON are refused rather than parsed. Containment, read, size and UTF-8 failures
  exit 2 (usage/environment); a capture that does not parse, or parses but fails
  validation, exits 1 (see the table in §4).

## 4. Running it

```sh
python3 infra/scoring/score.py --check-criteria          # validates criteria.json, exit 0
python3 infra/scoring/score.py infra/scoring/captures/*.json
python3 infra/scoring/score.py --captures-dir /some/dir captures/*.json   # tests only
python3 -m unittest infra.scoring.test_score
python3 -W error::ResourceWarning infra/scoring/score.py infra/scoring/captures/*.json
```

Exit codes, exactly as the code behaves:

| code | meaning |
|---|---|
| `0` | scored, and the top-ranked provider is eligible |
| `1` | **content**: the top-ranked provider failed or left a hard gate unknown, or a capture parsed as JSON but did not validate, or a capture did not parse as JSON, or `criteria.json` failed validation |
| `2` | **environment/usage**: a bad argument, too many captures, a capture outside the captures directory (traversal/subdirectory), a symlink or non-regular file, a file over the per-file or aggregate 4 MiB cap, invalid UTF-8, an unreadable `criteria.json`, or a `criteria.json` that does not parse |

The same inputs always produce byte-identical output (no timestamps,
order-independent ranking with a full tie-break), so the ranking is reproducible
from the captures alone, offline.

**Tie-break** (`score.py::_tie_key`, in this order): latency to the APIs, then
GPU price, then internet egress price, then provider name for determinism —
applied **only inside a declared noise band** (`params.noise_tolerance`, 0.01),
and with any dimension dropped for the whole band when a member has no number for
it. Outside a band the higher total wins. The record may quote this output; it
must never present a prose override as if the script had produced it.

## 5. Cost model

Not a price assertion — the formula this task supplies the numerator for:

```
cost/video = (GPU price per hour x node hours) / videos per hour
```

`videos per hour` is 11.6.3-T1's measured denominator; `node hours` follow the
duty cycle in `criteria.json.params` (6 h/day, 30.4375 days/month), and the
duty-cycle sensitivity feeds 11.2.2-T4's decision to keep staging GPU-free (F08).

## 6. Node shape this comparison fixes

To compare like with like, the capture files score one node per provider: **one
GPU with ≥ 16 GB VRAM, ≥ 8 vCPU and ≥ 32 GiB system RAM** (MPT + VieNeu + whisper
+ ffmpeg on one host; the 16 GiB-RAM single-GPU sizes are excluded by the shape,
not by the rules).

| provider | shape | GPU | vCPU / RAM | eligible |
|---|---|---|---|---|
| aws | `g4dn.2xlarge` (ap-southeast-1) | 1 x NVIDIA T4 16 GiB | 8 / 32 GiB | **no** — `owner_locked_eligible` (D21: no AWS account) + `block_volume_two_zones` unknown |
| azure | `Standard_NC8as_T4_v3` (southeastasia) | 1 x NVIDIA Tesla T4 16 GiB | 8 / 56 GiB | yes |
| gcp | `g2-standard-8` (asia-southeast1) | 1 x NVIDIA L4 24 GiB | 8 / 32 GiB | **no** — `pg17_pitr` unknown (E10 mechanism not evidenced) |

## 7. ⚠️ items in the exit artefact

The task allows ⚠️ for the latency numbers, the FQDN-egress answer (F14), the
private CA for `storage-<env>.internal` (T81) and T74's two-zone/location
support. What the captures actually carry:

| item | state |
|---|---|
| latency (`latency.md`) | ⚠️ **not measured** — the exact commands are the mandatory pre-apply check for 11.2.2-T1 step 1; `null_policy: neutral` |
| FQDN egress (F14) | aws `true` (Network Firewall domain lists, quoted), azure `true` (Firewall FQDN filtering, quoted), gcp `null` ⚠️ (only a navigation entry, not enough) |
| private CA (T81) | aws `true` at $400/CA-month, gcp `true` at $20/CA-month, azure `false` (no managed private CA) → for the finalist the T81 **offline-root fallback** applies; never client pinning |
| two zones (T74) | azure `true` with the region's availability-zone count quoted; gcp `true` with three zones listed; **aws `null` ⚠️** (only generic "a region has several AZs" wording, which is not region-specific evidence). The **provisioning** proof belongs to 11.2.2-T1 |
| PostgreSQL 17 PITR (E10) | azure `true` (base backup + WAL archiving quoted), aws `true` (transaction logs to S3 every five minutes, quoted), gcp `null` ⚠️ **unknown** — no fetchable page states the base-backup + continuous-WAL mechanism, so GCP is ineligible rather than assumed |
| payment from Vietnam | azure `true` and gcp `true` (official payment/currency tables quoted), aws `null` ⚠️ (the page is JavaScript-rendered and no quotable line could be read) |
| g5/g6 absence in `ap-southeast-1` | evidence is the AWS region price feed listing no g5/g6 entry; recorded as a feed fact, and the shape choice that follows from it is labelled `[INFERENCE]` |
