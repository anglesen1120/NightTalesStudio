# Region latency to Vietnam, OpenAI and YouTube — ⚠️ not yet measured

**Status: ⚠️ outstanding.** 11.2.1-T1 rents nothing (spend is capped at one
smallest instance per finalist region for under an hour, and the task's plan is
to run this before the decision is applied), so no number below is a measurement.
Every value in the captures is `null` for `latency_to_vn_ms` and
`latency_to_apis_ms`, and both latency criteria score neutral for every provider
(`score.py` treats "no evidence for anyone" as 1.0 each, so the missing
measurement cannot fake a win).

This file is therefore a **mandatory pre-apply check for 11.2.2-T1 step 1**: rent
one smallest instance in the chosen region, run the commands below, record the
raw numbers here with their timestamps, then destroy the instance. The decision
in `DECISIONS.md` §10.4 is provisional on this check only.

## Finalist regions

| provider | region | node shape | state |
|---|---|---|---|
| azure | `southeastasia` (Singapore) | `Standard_NC8as_T4_v3` | **the decision** (eligible) |
| gcp | `asia-southeast1` (Singapore) | `g2-standard-8` | runner-up, currently ineligible (`pg17_pitr` unknown) |
| aws | `ap-southeast-1` (Singapore) | `g4dn.2xlarge` | scored but **never eligible** (locked D21 "no AWS account") — rent nothing here |

This check decides two things: (1) the decision itself, because it is the one
evidence item the task defers (the record is provisional on it alone), and
(2) the order between azure and its runner-up, whose scored totals are far apart
but whose latency is the tie-break's first key.

## Method

From a temporary instance **inside the candidate region**, measure:

1. **Region → OpenAI** — TCP connect and total time to `api.openai.com`.
2. **Region → YouTube Data API** — the same for `www.googleapis.com`.
3. **Region → the VN vantage point** — ICMP to the operator's VN egress address
   (the latency that the owner and the upload path see), plus TCP connect for a
   host that blocks ICMP.

Run each at least 20 times, in the same region-rented hour, and record the
median and the p95 — a single number is not evidence.

## The exact commands

```sh
# 1. OpenAI (TCP connect + total, 20 runs; %{time_connect} is the network part)
for i in $(seq 20); do
  curl -s -o /dev/null -w '%{time_connect} %{time_total}\n' https://api.openai.com/v1/models
  sleep 1
done

# 2. YouTube Data API
for i in $(seq 20); do
  curl -s -o /dev/null -w '%{time_connect} %{time_total}\n' 'https://www.googleapis.com/youtube/v3/videos?part=id&id=dQw4w9WgXcQ'
  sleep 1
done

# 3a. VN vantage point, ICMP (replace <VN_EGRESS_HOST> with the operator's VN host)
ping -c 20 <VN_EGRESS_HOST>

# 3b. VN vantage point, TCP connect when ICMP is blocked
for i in $(seq 20); do
  curl -s -o /dev/null -w '%{time_connect} %{time_total}\n' https://<VN_EGRESS_HOST>/
  sleep 1
done

# 4. Reference: the same three commands from the implementation Mac, same hour
#    (a baseline so a bad number can be attributed to the region or to the day)
```

## Raw numbers

_No rows yet — this table is filled by the 11.2.2-T1 step-1 check. Do not invent
a value here._

| run (UTC) | provider/region | instance | openai connect/total (median, p95) | googleapis connect/total (median, p95) | VN ping (median, p95) | notes |
|---|---|---|---|---|---|---|
| _(pending)_ | azure `southeastasia` | `Standard_NC8as_T4_v3` | | | | the decision; run this first |
| _(pending)_ | gcp `asia-southeast1` | `g2-standard-8` | | | | runner-up, only if its PITR gate is evidenced |
| _(pending)_ | aws `ap-southeast-1` | `g4dn.2xlarge` | | | | informational only — AWS is ineligible (locked D21) |

## Threshold and consequence

`criteria.json.params.latency_threshold_ms` = **350 ms** (connect + total to
either API from the node). If the finalist exceeds it, the provider is demoted in
the record and the revisit-trigger "latency above the stated threshold" fires
(`DECISIONS.md` §10.4.1 trigger 2). If a runner-up beats the finalist on the
measured numbers *and* clears its own open gate, the runner-up is applied from the
same captures — the two latency criteria are `null_policy: neutral` today, so the
deferred measurement currently penalises nobody and this check is what turns the
provisional decision into a settled one.

## Cleanup

Destroy the rented instance immediately after the run; that cleanup is the only
rollback this task has (it creates no other cloud resource).
