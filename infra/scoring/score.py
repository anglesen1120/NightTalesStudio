#!/usr/bin/env python3
"""11.2.1-T1 — provider scoring harness.

Reads ``criteria.json`` (next to this file) plus one capture file per provider
(``captures/<provider>-<date>.json``), applies the hard gates, normalises every
scored criterion across providers, multiplies by the weight and prints a ranked
table plus a per-criterion diff.

Deterministic and stdlib-only: the same inputs always produce byte-identical
output, so a later price change is a new capture file plus a re-run.

Exit codes: 0 = scored and the top-ranked provider is eligible, 1 = the
top-ranked provider failed or has an unknown hard gate, or a capture is invalid,
2 = usage/environment error.

Hard gates
    A provider that fails any gate is not eligible. A gate whose capture value is
    null or missing is UNKNOWN, which also blocks eligibility; the table marks it
    with a warning instead of pretending it passed.

Null policy (declared per criterion as ``null_policy`` in criteria.json)
    ``neutral``  a null value scores 1.0 for that provider even when other
                 providers have numbers (the two latency items are ⚠️ deferred by
                 the task; charging them 0.0 would bake an artefact into the
                 ranking).
    ``fail``     a null value scores 0.0 when at least one provider has a number,
                 and is neutral (1.0) for every provider when all of them are
                 null; the report says so, because a criterion nobody answered
                 must not decide anything.

Ranking
    Providers are ranked by total score, then grouped into a noise band
    (``params.noise_tolerance``): every provider within the band of the best total
    is a tie, and the tie is broken by the declared order — latency to the APIs,
    then GPU price, then internet egress, then provider name. The band is applied
    by this script, never by prose outside it.
"""

import argparse
import datetime
import json
import math
import os
import re
import stat
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
DEFAULT_CRITERIA = os.path.join(HERE, "criteria.json")
DEFAULT_CAPTURES_DIR = os.path.join(HERE, "captures")

# T74 / 11 CR-15: the storage decision is a block-volume decision plus the
# media egress price. An object-storage product must never appear in the
# checklist file; this list guards that rule.
BANNED_TERMS = (
    "object storage",
    "object_storage",
    "s3-compatible",
    "s3 compatible",
    "minio",
    "seaweedfs",
    "wasabi",
    "backblaze",
    "ceph",
    "glacier",
    "blob storage",
    "digitalocean spaces",
    "spaces bucket",
    "cloudflare r2",
)

EXIT_OK = 0
EXIT_FAIL = 1
EXIT_USAGE = 2

# Capture input limits: the captures are a handful of small JSON files inside
# one directory. Anything larger, deeper, symlinked, duplicate-keyed or outside
# that directory is refused rather than parsed.
MAX_CAPTURE_FILES = 32
MAX_CAPTURE_BYTES = 4 * 1024 * 1024
MAX_JSON_DEPTH = 64
MAX_CHECKS = 64
NULL_POLICIES = ("neutral", "fail")

PROVIDER_RE = re.compile(r"^[a-z0-9-]{1,32}$")
DATE_RE = re.compile(r"^\d{4}-\d{2}-\d{2}$")
ID_RE = re.compile(r"^[a-z][a-z0-9_]{0,63}$")
DERIVED_RE = re.compile(r"^([A-Za-z0-9_]+)\s*((?:\*\s*params\.[A-Za-z0-9_]+\s*)+)$")
PARAM_RE = re.compile(r"params\.([A-Za-z0-9_]+)")

ENTRY_FIELDS = ("value", "unit", "source_url", "captured_on", "captured_by", "quote", "note")
CAPTURE_FIELDS = ("provider", "region", "captured_on", "captured_by", "node_shape", "values", "note")


def _err(msg):
    print("error: " + msg, file=sys.stderr)


def _reject_constant(name):
    raise ValueError("non-finite JSON constant %r (NaN/Infinity are not allowed)" % name)


def _reject_duplicate_keys(pairs):
    seen = set()
    for key, _ in pairs:
        if key in seen:
            raise ValueError("duplicate JSON key %r (a later value would silently win)" % key)
        seen.add(key)
    return dict(pairs)


def _valid_date(value):
    """True for a YYYY-MM-DD string that is a real calendar date."""
    if not isinstance(value, str) or not DATE_RE.match(value):
        return False
    try:
        datetime.date.fromisoformat(value)
    except ValueError:
        return False
    return True


def _bracket_depth(text):
    """Maximum nesting depth outside strings, or None when the text is unbalanced."""
    depth = 0
    max_depth = 0
    in_string = False
    escaped = False
    for ch in text:
        if in_string:
            if escaped:
                escaped = False
            elif ch == "\\":
                escaped = True
            elif ch == '"':
                in_string = False
            continue
        if ch == '"':
            in_string = True
        elif ch in "{[":
            depth += 1
            max_depth = max(max_depth, depth)
        elif ch in "}]":
            depth -= 1
            if depth < 0:
                return None
    if depth != 0 or in_string:
        return None
    return max_depth


def _loads(text, where):
    """Parse JSON, refusing non-finite constants, duplicate keys and absurd nesting.

    Returns (obj, error_message). The depth guard runs before the parser so a
    deeply nested document cannot reach the recursion limit at all; RecursionError
    is still caught for the pathological cases the scan cannot see. Duplicate keys
    are refused because a later value would silently win over the earlier one.
    """
    depth = _bracket_depth(text)
    if depth is None:
        return None, "%s: unbalanced JSON brackets" % where
    if depth > MAX_JSON_DEPTH:
        return None, "%s: JSON nesting depth %d exceeds the limit %d" % (where, depth, MAX_JSON_DEPTH)
    try:
        obj = json.loads(
            text,
            parse_constant=_reject_constant,
            object_pairs_hook=_reject_duplicate_keys,
        )
        return obj, None
    except RecursionError:
        return None, "%s: JSON nesting is too deep to parse" % where
    except ValueError as exc:
        return None, "%s: %s" % (where, exc)


def _secure_read(path, where, expect_parent=None):
    """Open one input file without following symlinks and read it under a cap.

    Returns (text, error, bytes_read): the caller accumulates ``bytes_read`` so the
    aggregate cap is enforced on bytes actually read, never on a pre-open stat that
    could change underneath. The checks and the read share one descriptor
    (`os.open` with `O_NOFOLLOW`, then `fstat` on that fd), so a path swapped
    between checking and reading (TOCTOU) cannot get past them. With
    ``expect_parent`` the file must be a direct child of that directory, compared
    by the parent's st_dev/st_ino, which is also the traversal/containment rule.
    Bytes are decoded here, so invalid UTF-8 is a read error, not a traceback.
    """
    try:
        fd = os.open(path, os.O_RDONLY | os.O_NOFOLLOW)
    except OSError as exc:
        return None, "%s: cannot open: %s" % (where, exc), 0
    try:
        st = os.fstat(fd)
        if not stat.S_ISREG(st.st_mode):
            return None, "%s: not a regular file" % where, 0
        if st.st_size > MAX_CAPTURE_BYTES:
            return None, (
                "%s: file is %d bytes, over the %d-byte cap" % (where, st.st_size, MAX_CAPTURE_BYTES)
            ), 0
        if expect_parent is not None:
            try:
                want = os.stat(expect_parent)
                parent = os.stat(os.path.dirname(os.path.abspath(path)))
            except OSError as exc:
                return None, "%s: cannot stat its directory: %s" % (where, exc), 0
            if (parent.st_dev, parent.st_ino) != (want.st_dev, want.st_ino):
                return None, (
                    "%s: outside the captures directory (no traversal, no subdirectories)" % where
                ), 0
        chunks = []
        total = 0
        while total <= MAX_CAPTURE_BYTES:
            chunk = os.read(fd, MAX_CAPTURE_BYTES + 1 - total)
            if not chunk:
                break
            chunks.append(chunk)
            total += len(chunk)
        if total > MAX_CAPTURE_BYTES:
            return None, "%s: grew past the %d-byte cap while reading" % (where, MAX_CAPTURE_BYTES), total
    finally:
        os.close(fd)
    try:
        return b"".join(chunks).decode("utf-8"), None, total
    except UnicodeDecodeError as exc:
        return None, "%s: not valid UTF-8: %s" % (where, exc), total


def _capture_path_errors(path, captures_dir):
    """Cheap shape checks for one capture argument, before opening it.

    Refuses symlinks, directories and anything not under the captures directory;
    `_secure_read` re-derives the same conclusions from the descriptor, so a swap
    between these checks and the read cannot slip through.
    """
    errors = []
    if os.path.islink(path):
        errors.append("%s: refusing a symlink (captures must be regular files)" % path)
        return errors
    if not os.path.isfile(path):
        errors.append("%s: not a regular file" % path)
        return errors
    base = os.path.realpath(captures_dir)
    real = os.path.realpath(path)
    if os.path.islink(captures_dir) or os.path.dirname(real) != base:
        errors.append(
            "%s: outside the captures directory %s (no traversal, no subdirectories)" % (path, captures_dir)
        )
    return errors


PARAM_RANGES = (
    # name, low, high, low_inclusive, high_inclusive, required (optional ones are checked when present)
    ("duty_cycle_hours_per_day", 0.0, 24.0, False, True, True),
    ("days_per_month", 0.0, 366.0, False, True, True),
    ("noise_tolerance", 0.0, 1.0, True, False, True),
    ("gpu_vram_min_gb", 0.0, 1024.0, False, True, False),
    ("gpu_vram_preferred_gb", 0.0, 1024.0, False, True, False),
    ("gpu_per_node", 1.0, 16.0, True, True, False),
    ("latency_threshold_ms", 0.0, 10000.0, False, True, False),
)


def _parse_derived(expr):
    """Parse the one supported derived-expression grammar.

    Grammar: ``<criterion_id> * params.<name> [* params.<name>]...`` — exactly one
    source criterion id and one or more multipliers, each a numeric entry in
    ``params``. Returns (source_id, [param names]) or raises ValueError, so a typo
    is a validation error instead of a silent None or a TypeError.
    """
    if not isinstance(expr, str):
        raise ValueError("derived must be a string")
    match = DERIVED_RE.match(expr)
    if not match:
        raise ValueError(
            "unsupported derived expression %r (expected '<criterion_id> * params.<name> ...')" % expr
        )
    names = PARAM_RE.findall(match.group(2))
    if not names:
        raise ValueError("derived expression %r has no params.<name> multiplier" % expr)
    return match.group(1), names


def check_criteria(criteria, raw_text):
    """Validate criteria.json. Returns a list of error strings (empty = ok).

    Everything the scorer executes is checked here: the gate thresholds, every
    param it multiplies or compares with, every enum score it indexes, and the
    derived-expression grammar. A malformed file therefore fails with a readable
    error list (exit 1) instead of a TypeError deep inside the scoring run.
    """
    errors = []
    low = raw_text.lower()
    for term in BANNED_TERMS:
        if term in low:
            errors.append("criteria file mentions a forbidden storage term: %r" % term)

    if not isinstance(criteria, dict):
        return ["criteria.json must be a JSON object"]
    gates = criteria.get("hard_gates")
    crits = criteria.get("criteria")
    if not isinstance(gates, list) or not gates:
        errors.append("hard_gates must be a non-empty list")
        gates = []
    if not isinstance(crits, list) or not crits:
        errors.append("criteria must be a non-empty list")
        crits = []
    if len(gates) > MAX_CHECKS:
        errors.append("hard_gates has %d entries, over the %d cap" % (len(gates), MAX_CHECKS))
    if len(crits) > MAX_CHECKS:
        errors.append("criteria has %d entries, over the %d cap" % (len(crits), MAX_CHECKS))

    params = criteria.get("params")
    if not isinstance(params, dict):
        errors.append("params must be an object")
        params = {}
    else:
        for name, low_bound, high_bound, low_inc, high_inc, required in PARAM_RANGES:
            if not required and name not in params:
                continue
            value = params.get(name)
            left = "[" if low_inc else "("
            right = "]" if high_inc else ")"
            if not _is_number(value):
                errors.append(
                    "params.%s must be a finite number in %s%s, %s%s" % (name, left, low_bound, high_bound, right)
                )
                continue
            if (value < low_bound) or (value == low_bound and not low_inc):
                errors.append(
                    "params.%s must be a finite number in %s%s, %s%s" % (name, left, low_bound, high_bound, right)
                )
            elif (value > high_bound) or (value == high_bound and not high_inc):
                errors.append(
                    "params.%s must be a finite number in %s%s, %s%s" % (name, left, low_bound, high_bound, right)
                )
        if _is_number(params.get("gpu_per_node")) and params["gpu_per_node"] != int(params["gpu_per_node"]):
            errors.append("params.gpu_per_node must be a whole number")
        vmin, vpref = params.get("gpu_vram_min_gb"), params.get("gpu_vram_preferred_gb")
        if _is_number(vmin) and _is_number(vpref) and vpref < vmin:
            errors.append("params.gpu_vram_preferred_gb must be >= params.gpu_vram_min_gb")
        for name, value in params.items():
            if name in [entry[0] for entry in PARAM_RANGES]:
                continue
            if value is not None and not _is_number(value):
                errors.append("params.%s must be a finite number or null" % name)

    def _valid_id(entry):
        return isinstance(entry, dict) and isinstance(entry.get("id"), str) and bool(ID_RE.match(entry["id"]))

    known_ids = [g["id"] for g in gates if _valid_id(g)]
    known_ids += [c["id"] for c in crits if _valid_id(c)]
    numeric_ids = {g["id"] for g in gates if _valid_id(g) and g.get("kind") == "min_number"}
    numeric_ids |= {c["id"] for c in crits
                    if _valid_id(c) and c.get("unit") not in ("bool", "enum") and c.get("scale") is None}

    seen = set()
    for gate in gates:
        if not _valid_id(gate):
            errors.append("every hard gate needs an id matching %s" % ID_RE.pattern)
            continue
        if gate["id"] in seen:
            errors.append("duplicate id: %s" % gate["id"])
        seen.add(gate["id"])
        kind = gate.get("kind")
        if kind not in ("bool", "min_number"):
            errors.append("hard gate %s has unknown kind" % gate["id"])
        if kind == "min_number" and not _is_number(gate.get("threshold")):
            errors.append("hard gate %s: min_number threshold must be a finite number" % gate["id"])
        elif kind == "bool" and gate.get("threshold") is not None:
            errors.append("hard gate %s: a bool gate must not declare a threshold" % gate["id"])
        if not gate.get("heading"):
            errors.append("hard gate %s has no heading" % gate["id"])
        if not gate.get("unit"):
            errors.append("hard gate %s has no unit" % gate["id"])

    total = 0.0
    for crit in crits:
        if not _valid_id(crit):
            errors.append("every criterion needs an id matching %s" % ID_RE.pattern)
            continue
        cid = crit["id"]
        if cid in seen:
            errors.append("duplicate id: %s" % cid)
        seen.add(cid)
        if not crit.get("heading"):
            errors.append("criterion %s has no heading" % cid)
        if crit.get("direction") not in ("lower", "higher"):
            errors.append("criterion %s has unknown direction" % cid)
        if not crit.get("unit"):
            errors.append("criterion %s has no unit" % cid)
        if crit.get("null_policy") not in NULL_POLICIES:
            errors.append(
                "criterion %s must declare null_policy as one of %s" % (cid, ", ".join(NULL_POLICIES))
            )
        scale = crit.get("scale")
        if scale is not None:
            if not isinstance(scale, dict) or not scale:
                errors.append("criterion %s has an empty or non-object scale" % cid)
            else:
                for key, score in scale.items():
                    if not isinstance(key, str) or not key.strip():
                        errors.append("criterion %s: scale keys must be non-empty strings" % cid)
                    if not _is_number(score) or not (0.0 <= score <= 1.0):
                        errors.append(
                            "criterion %s: scale[%r] must be a finite number in [0, 1]" % (cid, key)
                        )
        if crit.get("derived"):
            try:
                source, names = _parse_derived(crit["derived"])
            except ValueError as exc:
                errors.append("criterion %s: %s" % (cid, exc))
            else:
                if source not in known_ids:
                    errors.append("criterion %s: derived source %r is not a known id" % (cid, source))
                elif source == cid:
                    errors.append("criterion %s: derived cannot reference itself" % cid)
                elif source not in numeric_ids:
                    errors.append("criterion %s: derived source %r must be numeric (not bool or enum)" % (cid, source))
                for name in names:
                    if name not in params:
                        errors.append("criterion %s: derived references unknown params.%s" % (cid, name))
                    elif not _is_number(params.get(name)):
                        errors.append(
                            "criterion %s: derived multiplier params.%s must be a finite number" % (cid, name)
                        )
        weight = crit.get("weight")
        if not isinstance(weight, (int, float)) or isinstance(weight, bool) or weight <= 0:
            errors.append("criterion %s has no positive numeric weight" % cid)
            continue
        total += weight
    if abs(total - 1.0) > 1e-9:
        errors.append("weights sum to %.12f, expected exactly 1.0" % total)
    return errors


def _is_number(value):
    return isinstance(value, (int, float)) and not isinstance(value, bool) and math.isfinite(value)


def _value_errors(cid, entry, spec_kind, scale=None):
    """Type checks for one value entry. Returns a list of errors."""
    errors = []
    value = entry.get("value")
    if value is None:
        return errors
    if spec_kind == "bool":
        if not isinstance(value, bool):
            errors.append("value must be true/false or null for a bool entry")
    elif spec_kind == "number":
        if not _is_number(value):
            errors.append("value must be a finite number or null")
    elif spec_kind == "enum":
        if not isinstance(value, str) or value not in (scale or {}):
            errors.append("value must be one of the declared scale keys or null")
    return errors


def _gate_status(capture, gates):
    """Per-gate status: 'pass', 'fail' or 'unknown' (null/missing value)."""
    values = capture.get("values") or {}
    status = {}
    for gate in gates:
        gid = gate["id"]
        entry = values.get(gid)
        if not isinstance(entry, dict) or entry.get("value") is None:
            status[gid] = "unknown"
            continue
        value = entry["value"]
        if gate.get("kind") == "min_number":
            status[gid] = "pass" if _is_number(value) and value >= gate.get("threshold", 0) else "fail"
        else:
            status[gid] = "pass" if value is True else "fail"
    return status


def _gate_failures(capture, gates):
    """The gates this capture definitely fails (unknown gates excluded)."""
    status = _gate_status(capture, gates)
    return [g for g in (gate["id"] for gate in gates) if status[g] == "fail"]


def _gate_unknowns(capture, gates):
    status = _gate_status(capture, gates)
    return [g for g in (gate["id"] for gate in gates) if status[g] == "unknown"]


def crit_by_id(criteria):
    """One map id -> {kind, unit, scale, criterion or gate} built once."""
    out = {}
    for gate in criteria["hard_gates"]:
        out[gate["id"]] = {
            "kind": "bool" if gate.get("kind") == "bool" else "number",
            "unit": gate.get("unit"),
            "scale": None,
            "crit": gate,
        }
    for crit in criteria["criteria"]:
        unit = crit.get("unit")
        out[crit["id"]] = {
            "kind": "bool" if unit == "bool" else ("enum" if crit.get("scale") else "number"),
            "unit": unit,
            "scale": crit.get("scale"),
            "crit": crit,
        }
    return out


def validate_capture(capture, path, criteria, spec=None):
    """Return a list of validation errors for one capture file.

    The schema is exact: the top-level shape, the value keys (equal to the gate +
    criterion ids), the field names inside each entry, and every field's type are
    all required, so a malformed capture cannot quietly score something else.
    """
    errors = []
    if not isinstance(capture, dict):
        return ["%s: capture must be a JSON object" % path]
    spec = spec if spec is not None else crit_by_id(criteria)

    for key in sorted(set(capture) - set(CAPTURE_FIELDS)):
        errors.append("%s: unknown top-level key %r" % (path, key))
    provider = capture.get("provider")
    if not isinstance(provider, str) or not PROVIDER_RE.match(provider):
        errors.append("%s: provider must match ^[a-z0-9-]{1,32}$ (got %r)" % (path, provider))
    region = capture.get("region")
    if not isinstance(region, str) or not region.strip():
        errors.append("%s: region must be a non-empty string" % path)
    if capture.get("captured_by") is not None and not isinstance(capture.get("captured_by"), str):
        errors.append("%s: captured_by must be a string when present" % path)
    if capture.get("node_shape") is not None and not isinstance(capture.get("node_shape"), str):
        errors.append("%s: node_shape must be a string when present" % path)
    captured_on = capture.get("captured_on")
    if not _valid_date(captured_on):
        errors.append("%s: captured_on must be a YYYY-MM-DD date (got %r)" % (path, captured_on))

    values = capture.get("values")
    if not isinstance(values, dict) or not values:
        errors.append("%s: values must be a non-empty object" % path)
        return errors
    if len(values) > MAX_CHECKS:
        errors.append("%s: values has %d entries, over the %d cap" % (path, len(values), MAX_CHECKS))

    for cid in sorted(set(values) - set(spec)):
        errors.append("%s: unknown id %r (not a hard gate or criterion)" % (path, cid))
    for cid in sorted(set(spec) - set(values)):
        errors.append("%s: missing %r (every gate and criterion needs an entry)" % (path, cid))

    for cid in sorted(set(values) & set(spec)):
        entry = values[cid]
        if not isinstance(entry, dict):
            errors.append("%s: %s must be an object" % (path, cid))
            continue
        for field in sorted(set(entry) - set(ENTRY_FIELDS)):
            errors.append("%s: %s has unknown field %r" % (path, cid, field))
        for field in ("value", "unit", "source_url", "captured_on", "captured_by", "quote"):
            if field not in entry:
                errors.append("%s: %s is missing the field %r" % (path, cid, field))
        for field in ("source_url", "quote", "captured_on", "captured_by"):
            if field in entry and not (isinstance(entry[field], str) and entry[field].strip()):
                errors.append("%s: %s: %s must be a non-empty string" % (path, cid, field))
        if entry.get("note") is not None and not isinstance(entry.get("note"), str):
            errors.append("%s: %s: note must be a string when present" % (path, cid))
        if "unit" in entry:
            unit = entry["unit"]
            if not isinstance(unit, str) or unit != spec[cid]["unit"]:
                errors.append(
                    "%s: %s: unit %r does not match the declared unit %r"
                    % (path, cid, unit, spec[cid]["unit"])
                )
        if entry.get("captured_on") is not None and entry["captured_on"] != captured_on:
            errors.append(
                "%s: %s: captured_on %r differs from the capture date %r"
                % (path, cid, entry["captured_on"], captured_on)
            )
        if "value" in entry:
            errors.extend(
                "%s: %s: %s" % (path, cid, e)
                for e in _value_errors(cid, entry, spec[cid]["kind"], spec[cid]["scale"])
            )

    # A derived criterion must be derived, never hand-typed: the capture's own
    # value has to equal (gpu_price_per_hour x duty cycle x days per month).
    for crit in criteria["criteria"]:
        if not crit.get("derived"):
            continue
        cid = crit["id"]
        derived = _derive(crit, capture, criteria)
        entry = values.get(cid)
        if not isinstance(entry, dict):
            continue
        value = entry.get("value")
        if not _is_number(value):
            errors.append("%s: %s must carry the derived numeric value %r" % (path, cid, derived))
        elif derived is None or abs(value - derived) > max(1e-6, abs(derived) * 1e-6):
            errors.append(
                "%s: %s value %r does not match the derived %r" % (path, cid, value, derived)
            )
    return errors


def _derive(crit, capture, criteria):
    """Value of a derived criterion for one capture, or None when it cannot be derived."""
    params = criteria.get("params") or {}
    try:
        source, names = _parse_derived(crit.get("derived"))
    except (KeyError, ValueError, AttributeError):
        return None
    entry = (capture.get("values") or {}).get(source)
    value = entry.get("value") if isinstance(entry, dict) else None
    if not _is_number(value):
        return None
    total = value
    for name in names:
        factor = params.get(name)
        if not _is_number(factor):
            return None
        total *= factor
    return total


def _normalise(crit, values):
    """values: {provider: raw value}. Returns {provider: score in [0,1]}."""
    direction = crit["direction"]
    neutral = crit.get("null_policy") == "neutral"
    out = {}
    if crit.get("scale"):
        if all(v is None for v in values.values()):
            # Nobody answered: neutral for everyone (documented in the record).
            return {p: 1.0 for p in values}
        for provider, value in values.items():
            if value is None and neutral:
                out[provider] = 1.0
            else:
                out[provider] = float(crit["scale"].get(value, 0.0))
        return out
    if crit.get("unit") == "bool":
        answered = [v for v in values.values() if v is not None]
        if not answered:
            # Nobody answered: neutral for everyone (documented in the record).
            return {p: 1.0 for p in values}
        for provider, value in values.items():
            if value is None and neutral:
                out[provider] = 1.0
            else:
                out[provider] = 1.0 if value is True else 0.0
        return out
    numeric = {p: v for p, v in values.items() if _is_number(v)}
    if not numeric:
        # No evidence at all: neutral for every provider (documented in the record).
        return {p: 1.0 for p in values}
    low, high = min(numeric.values()), max(numeric.values())
    if high == low:
        # No spread to normalise: every measured provider is equal-best, and an
        # unmeasured one still follows its declared null policy.
        return {
            p: (1.0 if _is_number(v) or neutral else 0.0)
            for p, v in values.items()
        }
    for provider, value in values.items():
        if not _is_number(value):
            # Unmeasured: neutral only where the criterion declares it, else 0.0.
            out[provider] = 1.0 if neutral else 0.0
        elif direction == "lower":
            out[provider] = (high - value) / (high - low)
        else:
            out[provider] = (value - low) / (high - low)
    return out


def score_captures(criteria, captures):
    """Return (rows, criteria_detail). rows is ranked worst-last by total."""
    gates = criteria["hard_gates"]
    crits = criteria["criteria"]

    providers = [c["provider"] for c in captures]
    by_provider = {c["provider"]: c for c in captures}
    gate_fail = {c["provider"]: _gate_failures(c, gates) for c in captures}
    gate_unknown = {c["provider"]: _gate_unknowns(c, gates) for c in captures}

    rows = {
        p: {
            "provider": p,
            "total": 0.0,
            "gate_failures": gate_fail[p],
            "gate_unknown": gate_unknown[p],
        }
        for p in providers
    }

    detail = []
    for crit in crits:
        cid = crit["id"]
        raw = {}
        for c in captures:
            if crit.get("derived"):
                raw[c["provider"]] = _derive(crit, c, criteria)
            else:
                entry = (c.get("values") or {}).get(cid)
                raw[c["provider"]] = entry.get("value") if isinstance(entry, dict) else None
        scores = _normalise(crit, raw)
        for provider in providers:
            rows[provider]["total"] += crit["weight"] * scores.get(provider, 0.0)
        detail.append(
            {
                "id": cid,
                "weight": crit["weight"],
                "unit": crit.get("unit"),
                "null_policy": crit.get("null_policy"),
                "raw": raw,
                "scored": raw,
                "scores": scores,
            }
        )

    ordered = _rank(rows, by_provider, criteria)
    return ordered, detail


TIE_DIMENSIONS = ("latency_to_apis_ms", "gpu_price_per_hour", "internet_egress_gb")


def _numbers_for(capture, cids):
    """{cid: number or None} for one capture (no inf sentinel: None means unmeasured)."""
    values = capture.get("values") or {}
    out = {}
    for cid in cids:
        entry = values.get(cid)
        value = entry.get("value") if isinstance(entry, dict) else None
        out[cid] = value if _is_number(value) else None
    return out


def _band_dimensions(band, by_provider):
    """Tie-break dimensions usable for one band.

    A dimension is skipped for the whole band as soon as any member has no number
    for it: an unmeasured value must neither decide the order nor be scored as if
    it were infinitely bad. When every dimension is skipped the provider name is
    the last key, so the result is still deterministic.
    """
    dims = []
    for cid in TIE_DIMENSIONS:
        if all(_numbers_for(by_provider[row["provider"]], (cid,))[cid] is not None for row in band):
            dims.append(cid)
    return dims


def _tie_key(row, capture, dims):
    nums = _numbers_for(capture, dims)
    # Tie-break order (documented): latency to the APIs first, then the GPU
    # price, then the egress price, then the provider name for determinism.
    return tuple(nums[cid] for cid in dims) + (row["provider"],)


def _rank(rows, by_provider, criteria):
    """Rank by total, then resolve the declared noise band with the tie-break keys."""
    tolerance = criteria.get("params", {}).get("noise_tolerance", 0.0)
    if not _is_number(tolerance) or tolerance < 0:
        tolerance = 0.0
    ordered = sorted(rows.values(), key=lambda r: (-r["total"], r["provider"]))
    ranked = []
    band_no = 0
    index = 0
    while index < len(ordered):
        centre = ordered[index]["total"]
        band = [r for r in ordered[index:] if centre - r["total"] <= tolerance + 1e-9]
        dims = _band_dimensions(band, by_provider)
        band.sort(key=lambda r: _tie_key(r, by_provider[r["provider"]], dims))
        band_no += 1
        for row in band:
            row["band"] = band_no
            row["tie_dimensions"] = list(dims)
        ranked.extend(band)
        index += len(band)
    return ranked


def format_report(criteria, captures, ranked, detail):
    lines = []
    providers = [c["provider"] for c in captures]
    tolerance = criteria.get("params", {}).get("noise_tolerance", 0.0)
    lines.append("provider scoring — %d providers, criteria %s" % (len(providers), criteria.get("version", "?")))
    lines.append("hard gates: " + ", ".join(g["id"] for g in criteria["hard_gates"]))
    lines.append("noise tolerance: %.4f (a tie band is broken by latency, then GPU price, then egress)" % tolerance)
    lines.append("")
    header = "%-4s %-16s %8s  %-9s %s" % ("rank", "provider", "total", "eligible", "gate failures / unknown")
    lines.append(header)
    lines.append("-" * len(header))
    for i, row in enumerate(ranked, 1):
        if row["gate_failures"] or row["gate_unknown"]:
            eligible = "no"
        else:
            eligible = "yes"
        bits = list(row["gate_failures"])
        bits += ["%s (unknown)" % g for g in row["gate_unknown"]]
        lines.append(
            "%-4d %-16s %8.4f  %-9s %s"
            % (i, row["provider"], row["total"], eligible, ",".join(bits) if bits else "-")
        )
    lines.append("")
    lines.append("tie bands (tolerance %.4f):" % tolerance)
    for row in ranked:
        dims = row.get("tie_dimensions") or []
        lines.append(
            "    band %d  %-16s total=%.4f  tie-break keys: %s"
            % (row["band"], row["provider"], row["total"], ", ".join(dims) if dims else "(provider name only)")
        )
    lines.append("")
    lines.append("per-criterion diff (raw | normalised 0-1)")
    for crit in detail:
        lines.append(
            "%s  weight=%.2f  unit=%s  null_policy=%s"
            % (crit["id"], crit["weight"], crit["unit"], crit["null_policy"])
        )
        for provider in providers:
            raw = crit["raw"][provider]
            score = crit["scores"].get(provider, 0.0)
            lines.append("    %-16s raw=%-14s score=%.3f" % (provider, _fmt(raw), score))
    lines.append("")
    winner = next((r for r in ranked if not r["gate_failures"] and not r["gate_unknown"]), None)
    if winner is not None:
        lines.append("winner (top eligible): %s (total %.4f)" % (winner["provider"], winner["total"]))
    else:
        lines.append("winner (top eligible): none — every provider fails or leaves a hard gate unknown")
    for row in ranked:
        if row["gate_unknown"] and not row["gate_failures"]:
            lines.append(
                "warning: %s leaves a hard gate unknown: %s" % (row["provider"], ",".join(row["gate_unknown"]))
            )
    return "\n".join(lines)


def _fmt(value):
    if value is None:
        return "null"
    if isinstance(value, bool):
        return "true" if value else "false"
    if isinstance(value, float):
        return "%.6g" % value
    return str(value)


def main(argv=None):
    parser = argparse.ArgumentParser(description="11.2.1-T1 provider scoring harness")
    parser.add_argument("captures", nargs="*", help="capture JSON files (one per provider)")
    parser.add_argument("--criteria", default=DEFAULT_CRITERIA, help="criteria.json path")
    parser.add_argument(
        "--captures-dir",
        default=DEFAULT_CAPTURES_DIR,
        help="canonical directory every capture argument must live in (default: <script>/captures)",
    )
    parser.add_argument("--check-criteria", action="store_true", help="validate criteria.json and exit")
    args = parser.parse_args(argv)

    text, err, _ = _secure_read(args.criteria, args.criteria)
    if err:
        _err(err)
        return EXIT_USAGE
    criteria, perr = _loads(text, args.criteria)
    if perr:
        _err(perr)
        return EXIT_USAGE

    errors = check_criteria(criteria, text)
    if errors:
        for err in errors:
            _err(err)
        return EXIT_FAIL
    spec = crit_by_id(criteria)
    if args.check_criteria:
        print(
            "criteria ok: %d hard gates, %d criteria, weights sum to 1.0"
            % (len(criteria["hard_gates"]), len(criteria["criteria"]))
        )
        return EXIT_OK

    if not args.captures:
        _err("no capture files given (usage: score.py [--criteria FILE] [--captures-dir DIR] captures/*.json)")
        return EXIT_USAGE
    if len(args.captures) > MAX_CAPTURE_FILES:
        _err("too many capture files: %d, cap is %d" % (len(args.captures), MAX_CAPTURE_FILES))
        return EXIT_USAGE

    paths = []
    for path in args.captures:
        path_errors = _capture_path_errors(path, args.captures_dir)
        if path_errors:
            for err in path_errors:
                _err(err)
            return EXIT_USAGE
        paths.append(path)

    captures = []
    bad = False
    bytes_read = 0
    for path in paths:
        text, err, read_now = _secure_read(path, path, expect_parent=args.captures_dir)
        # The aggregate cap counts bytes actually read, not a pre-open stat.
        bytes_read += read_now
        if err:
            # unreadable, non-regular, undecodable or out of place: environment
            _err(err)
            return EXIT_USAGE
        if bytes_read > MAX_CAPTURE_BYTES:
            _err(
                "captures total %d bytes read, over the %d-byte cap"
                % (bytes_read, MAX_CAPTURE_BYTES)
            )
            return EXIT_USAGE
        capture, perr = _loads(text, path)
        if perr:
            _err(perr)
            bad = True
            continue
        errs = validate_capture(capture, path, criteria, spec)
        if errs:
            for err in errs:
                _err(err)
            bad = True
            continue
        captures.append(capture)
    if bad:
        return EXIT_FAIL
    names = [c["provider"] for c in captures]
    if len(set(names)) != len(names):
        _err("duplicate provider name across captures: %s" % ", ".join(sorted(names)))
        return EXIT_USAGE

    ranked, detail = score_captures(criteria, captures)
    print(format_report(criteria, captures, ranked, detail))

    top = ranked[0]
    if top["gate_failures"]:
        _err("top provider %s fails hard gates: %s" % (top["provider"], ",".join(top["gate_failures"])))
        return EXIT_FAIL
    if top["gate_unknown"]:
        _err(
            "top provider %s leaves hard gates unknown: %s"
            % (top["provider"], ",".join(top["gate_unknown"]))
        )
        return EXIT_FAIL
    return EXIT_OK


if __name__ == "__main__":
    sys.exit(main())
