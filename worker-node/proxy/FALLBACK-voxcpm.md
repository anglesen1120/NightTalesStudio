# FALLBACK — Option B: MPT `voxcpm` slot (WAV + SSE)

**Status:** point-in-time fallback design note, 2026-09-25. This is **not** an
approved plan and not a task in the M0/M1 delivery path — it blocks nothing
(04.3.1-T1 §"Failure handling"). It exists so that if option A (MPT `kokoro`
slot → this proxy → VieNeu PCM streaming) ever becomes blocked or unacceptable,
the Tech Lead can answer "should we switch now?" without reopening
`DECISIONS.md` §5.3 or the VieNeu source. DECISIONS §3 (VieNeu) is locked and
is **not** reopened by this note (README F04).

Legend: ✅ verified at the pinned commits (`worker-node/proxy/SPIKE.md`, VieNeu
`c1390abb`, MPT `3d5f4e4`) · ⚠️ stated in DECISIONS/README but with open risk ·
[OPEN] not yet investigated · [INFERENCE] my reasoning, not a measured fact.

## 1. When to switch

Concrete triggers only — not operational noise:

- **Owner-level:** VieNeu upstream is unmaintained/abandoned for > 2 weeks
  *with* an open security issue. Not automatic — the owner decides.
- **Voice quality:** the chosen VieNeu presets fail the English-narration bar
  and the owner picks O2 outcome "(c) reopen the locked TTS decision" (README
  §10 O2). DECISIONS §3 is locked; only the owner can reopen it.
- **Blocking defect:** a VieNeu-specific defect blocks M0/M1 with no workaround
  inside this plan's design — e.g. streaming PCM output is removed upstream,
  or the pinned commit's decode breaks and no other pin fixes it.

Explicitly **not** triggers:

- Normal 429/5xx operational noise — handled by D5 admission
  (`ADMISSION_WAIT_S` = 80 s, T15).
- A single bad voice rating — handled by 04.2.1-T1's per-voice re-audition and
  the D9 catalog.
- The disconnect/slot-release finding (SPIKE.md (d), T15): already absorbed by
  the T15 drain ruling in option A's D4.

## 2. MPT `[voxcpm]` facts (DECISIONS.md §5.3, cited not re-derived) ✅

- Body sent by MPT's voxcpm slot: `{"response_format": "wav", "stream": true}`,
  and an `api_key` is **required**.
- MPT parses SSE frames `{"type":"speech.audio.delta","audio":"<base64>"}` …
  `speech.audio.done` (`app/services/voice.py:2629-2713`).
- **One voice per config**: a single `voice_id` per `[voxcpm]` block
  (`voice.py:324-337`).
- VieNeu side: `POST /v1/audio/speech` accepts only `pcm`/`wav`; SSE is enabled
  by the body field `stream_format: "sse"` (default `"audio"`) — a field D3's
  upstream body never sends. ✅ at the pin (SPIKE V1: `stream_format` default
  `"audio"`, L229–242).

## 3. What the proxy would change (by D-name, against README §3)

- **D2 (routes/auth):** unchanged on the MPT face — `POST /v1/audio/speech`
  stays the route; Bearer `PROXY_API_KEY` auth, 404/405 allow-list and the
  error envelope all carry over.
- **D3 (request contract):** unchanged on the MPT face — the proxy still
  receives MPT's ordinary `mp3` request (D10, option A) and still speaks to
  VieNeu, **not** to voxcpm's own upstream. The change is in the upstream
  body: `response_format: "wav"` + `stream_format: "sse"` instead of D3's
  `response_format: "pcm"` + `stream_format: "audio"`. (DECISIONS §5.3
  describes option B as "only rewrites the JSON body: add
  `stream_format: "sse"`, drop `stream`" — there `stream` refers to MPT's
  voxcpm field, which never reaches the proxy in our design since MPT would
  still be configured on a slot that posts to the proxy. [INFERENCE]: the
  exact `[voxcpm]`-vs-`[kokoro]` MPT wiring for this variant needs resolving
  if pursued — see §5.)
- **D4 (streaming pipeline):** response parsing changes from raw PCM framing
  to SSE event parsing: accumulate `audio` (base64) from `speech.audio.delta`
  events until `speech.audio.done`, decode each chunk, and feed the decoded
  audio into the **same ffmpeg command** (D4) — reusable unchanged if the
  decoded deltas are s16le PCM. ⚠️ Whether VieNeu's SSE `audio` payloads are
  raw PCM or WAV fragments is **not** stated in SPIKE.md (audio mode tested
  only) — [OPEN]. If they are WAV, the pipeline needs `-f wav -i pipe:0`
  instead of `-f s16le …` and inherits the unknown-length WAV question (§5).
  The commit/first-audio gates (D4), the T15 drain rule, and `audio_seconds`
  accounting carry over conceptually.
- **D6 (error codes):** reusable as-is — `upstream_*`/`transcode_failed`/
  `client_disconnected` semantics are unchanged; only `upstream.py`'s
  `speech_stream` internals change, not the public contract to MPT.
- **D9 (catalog `voices.v1`):** schema unaffected — it already carries one
  canonical id per voice; the proxy's allow-list logic is unchanged.
- **D10 (MPT configuration):** this is where the cost lands. A `[voxcpm]`
  block selects **one** `voice_id`; switching narrators or languages means a
  different MPT config, not a different `voice_name` per job. The
  `voice_name = "kokoro:<id>"` render-submit contract (D10) would need a
  voxcpm-side equivalent keyed to that single configured voice.

## 4. Consequences

- **One voice per config** (§5.3) ⇒ the node needs **one MPT config profile
  per language** (a `[voxcpm]` block can hold only one `voice_id`), unlike
  today's single `[kokoro]` block with a catalog-driven list. This multiplies
  the node MPT config templates by language count and complicates
  01.2.2-T2 / 05.1.1 / 11.3.1.
- The voice catalog (D9) loses its "one config, many voices" simplicity on
  the MPT side: switching narrators would require **redeploying MPT config**,
  not just adding a catalog row. The backend catalog itself (`voices` table,
  previews, picker) is unaffected — voices are still VieNeu presets either
  way.
- **04.2-voice-catalog work stays reusable**: D11 audition protocol and D12
  previews (generated through the running proxy — the ffmpeg MP3 pipeline is
  the same) carry over; only the MPT-side selection mechanism changes.
- **Gained vs option A:** no ffmpeg WAV question on the MPT-facing path if the
  SSE deltas decode to PCM — and DECISIONS §5.3 lists "no ffmpeg in the
  proxy; key-based auth built in" as option B's pro (the voxcpm slot sends a
  WAV upstream on voxcpm's own deployment; in *our* proxy variant ffmpeg is
  still needed to turn VieNeu's pcm/wav audio into the `mp3` MPT's kokoro
  slot demands — unless MPT is genuinely switched to the `[voxcpm]` slot, in
  which case the proxy must speak SSE `speech.audio.*` frames *to MPT* and
  ffmpeg may be unnecessary. [INFERENCE] — there are **two distinct option-B
  shapes**; §5 keeps both open.)
- **Lost vs option A:** the D4 first-PCM-bytes empty-audio proof
  (`upstream_empty_audio`, F11) must be re-derived for the SSE path — the
  "first audio" signal becomes "first `speech.audio.delta` with non-empty
  decoded bytes". [INFERENCE]

## 5. What stays unverified

- ⚠️ **pydub decode of VieNeu's unknown-length WAV** (RIFF `0xFFFFFFFF` sizes)
  streamed via SSE deltas — DECISIONS §8 item 6 / CR-15 mark this ⚠️.
  04.1.1-T1 (SPIKE (a)) confirmed only that **ffmpeg** decodes that header
  (exit 0, duration Δ +26 ms, no warnings on this Mac); pydub and an
  SSE-reassembled stream were **not** tested.
- [OPEN] **SSE-mode semantics:** whether VieNeu's `stream_format: "sse"` keeps
  the same admission/429/abort/slot-release behaviour as `"audio"` mode
  (V3–V5, SPIKE (d)). The spike tested `"audio"` mode only.
- [OPEN] **Payload format inside SSE deltas** (PCM vs WAV fragments) — not
  read from the pinned `apps/openai_speech.py` for this note; determines
  whether D4's ffmpeg command is truly reusable unchanged.
- [OPEN] **MPT voxcpm client timeout/retry behaviour** (parallel to M2/M3 for
  kokoro) — whether it matches the 120 s-idle assumption this plan relies on
  (R1). Not read from MPT source; would need its own spike (parallel to
  04.1.1-T1) if option B is pursued.
- [OPEN] **Which option-B shape:** (i) keep MPT on `[kokoro]`/`mp3` and let
  the proxy transcode SSE audio → MP3 as today, or (ii) move MPT to
  `[voxcpm]`/`wav`+SSE and have the proxy translate VieNeu's SSE frames into
  voxcpm's `speech.audio.*` frame shape (no ffmpeg). DECISIONS §5.3's "only
  rewrites the JSON body" describes (ii) pointed at a voxcpm-style upstream;
  our upstream is VieNeu, so the proxy must still bridge.

## Sources

- `DECISIONS.md` §5.3 (MPT voxcpm/kokoro facts, VieNeu `stream_format`, the
  option A/B table) · §8 item 6 (WAV header ⚠️)
- `plans/features/04-voice-proxy/README.md` §3: D2, D3, D4 (incl. T15 drain),
  D5 (`ADMISSION_WAIT_S` = 80), D6, D9, D10 · F04, F11, F12, F17 · CR-15 ·
  O2 · T12/T15
- `worker-node/proxy/SPIKE.md` (04.1.1-T1): (a) PCM/WAV decode at pin
  `c1390abb`, (d) disconnect/slot release, V1/V3–V5 fact re-check
