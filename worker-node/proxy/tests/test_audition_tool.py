"""Tests for worker-node/proxy/tools/audition.py (04.2.1-T1).

No network: every test drives the CLI via subprocess against synthetic
snapshots/sheets; the real VieNeu is never touched.
"""
import csv
import importlib.util
import json
import os
import shlex
import subprocess
import sys
from pathlib import Path
TOOL = Path(__file__).resolve().parents[1] / "tools" / "audition.py"
SNAPSHOT = Path(__file__).resolve().parents[2] / "vieneu" / "voices.snapshot.json"

SHEET_FIELDS = ["sample_id", "rater", "naturalness", "intelligibility",
                "night_fit", "mispronunciations", "rater_tags", "notes"]


def run(*args, env_extra=None, cwd=None):
    env = dict(os.environ)
    env.pop("VIENEU_API_KEY", None)
    if env_extra:
        env.update(env_extra)
    return subprocess.run([sys.executable, str(TOOL), *args],
                          capture_output=True, text=True, env=env, cwd=cwd)


def snap_file(tmp_path, voices):
    p = tmp_path / "snap.json"
    p.write_text(json.dumps({"object": "list", "data": voices},
                            ensure_ascii=False), encoding="utf-8")
    return p


def test_audition_candidates_exclude_adam_bua_and_aliases(tmp_path):
    snap = snap_file(tmp_path, [
        {"id": "Adam bựa", "name": "Adam bựa", "aliases": ["ab"]},
        {"id": "Adam", "name": "Adam", "aliases": ["adam-en"]},
        {"id": "Thiện Minh", "name": "Thiện Minh", "aliases": ["tm"]},
    ])
    r = run("candidates", "--snapshot", str(snap))
    assert r.returncode == 0
    ids = r.stdout.splitlines()
    assert ids == ["Adam", "Thiện Minh"]
    assert "Adam bựa" not in ids and "ab" not in ids and "adam-en" not in ids


def test_audition_tags_from_description_maps_region_and_style(tmp_path):
    # "Nam · Nam · …" must read as male + southern (first Nam = male).
    snap = snap_file(tmp_path, [
        {"id": "V1", "description": "Nam · Nam · kể chuyện", "featured": True},
        {"id": "V2", "description": "Nữ · Bắc · truyền cảm", "featured": False},
    ])
    key = {"s-v1": {"voice_id": "V1", "language": "vi"},
           "s-v2": {"voice_id": "V2", "language": "vi"}}
    (tmp_path / "key.json").write_text(json.dumps(key))
    sheet = tmp_path / "sheet.vi.csv"
    with sheet.open("w", newline="", encoding="utf-8") as f:
        w = csv.writer(f)
        w.writerow(SHEET_FIELDS)
        w.writerow(["s-v1", "OW", 5, 5, 5, "", "deep", ""])
        w.writerow(["s-v2", "OW", 5, 5, 4, "", "warm", ""])
    out = tmp_path / "results.csv"
    r = run("unblind", "--key", str(tmp_path / "key.json"),
            "--sheets", str(sheet), "--snapshot", str(snap), "--out", str(out))
    assert r.returncode == 0
    rows = {row["voice_id"]: row for row in csv.DictReader(out.open())}
    assert rows["V1"]["gender"] == "male"
    assert rows["V1"]["region"] == "southern"
    assert rows["V1"]["style"] == "kể chuyện"
    r = run("summarize", "--results", str(out), "--snapshot", str(snap))
    assert r.returncode == 0
    vi_lines = [l for l in r.stdout.splitlines() if l.startswith("| V")]
    # V1: storyteller + southern (+ rater tag); V2: unmapped style → no
    # non-D9 tag leaks (must not contain "truyền cảm").
    assert "storyteller" in vi_lines[0] and "southern" in vi_lines[0]
    assert "truyền cảm" not in r.stdout


def test_audition_blind_ids_unique_and_unrelated_to_voice(tmp_path):
    key = {f"s-{i:06x}": {"voice_id": f"V{i}", "language": "vi"}
           for i in range(10)}
    (tmp_path / "key.json").write_text(json.dumps(key))
    r = run("sheet", "--out", str(tmp_path))
    assert r.returncode == 0
    sids = [row["sample_id"]
            for row in csv.DictReader((tmp_path / "sheet.vi.csv").open())]
    assert len(sids) == 10 and len(set(sids)) == 10
    assert all(s.startswith("s-") for s in sids)
    assert not any(f"V{i}" in s for i, s in enumerate(sids))
    pl = (tmp_path / "playlist.vi.m3u").read_text().split()
    assert pl == [f"{s}.mp3" for s in sids]  # playlist order == sheet order


def test_audition_missing_key_exits_2(tmp_path):
    r = run("generate", "--snapshot", str(SNAPSHOT), "--lang", "vi",
            "--out", str(tmp_path))
    assert r.returncode == 2
    assert "VIENEU_API_KEY" in r.stderr
    assert not list(tmp_path.glob("*.mp3"))


def _results(tmp_path, rows):
    out = tmp_path / "results.csv"
    with out.open("w", newline="", encoding="utf-8") as f:
        w = csv.writer(f)
        w.writerow(["language", "sample_id", "voice_id", "gender", "region",
                    "style", "rater", "naturalness", "intelligibility",
                    "night_fit", "mispronunciations", "rater_tags", "notes"])
        w.writerows(rows)
    return out


def test_audition_summarize_applies_thresholds_and_tiebreaks(tmp_path):
    snap = snap_file(tmp_path, [
        {"id": "PASS", "description": "Nam · Bắc · kể chuyện", "featured": True},
        {"id": "LOW_NAT", "description": "Nữ · Trung · tin tức", "featured": False},
        {"id": "LOW_INT", "description": "Nam · Nam · tự nhiên", "featured": False},
    ])
    res = _results(tmp_path, [
        ["vi", "s-1", "PASS", "male", "northern", "kể chuyện", "OW", "4", "5", "5", "", "", ""],
        ["vi", "s-1", "PASS", "male", "northern", "kể chuyện", "NS", "4", "4", "4", "", "", ""],
        ["vi", "s-2", "LOW_NAT", "female", "central", "tin tức", "OW", "3", "5", "5", "", "", ""],
        ["vi", "s-2", "LOW_NAT", "female", "central", "tin tức", "NS", "3", "5", "5", "", "", ""],
        ["vi", "s-3", "LOW_INT", "male", "southern", "tự nhiên", "OW", "5", "3", "5", "", "", ""],
        ["vi", "s-3", "LOW_INT", "male", "southern", "tự nhiên", "NS", "5", "3", "5", "", "", ""],
    ])
    r = run("summarize", "--results", str(res), "--snapshot", str(snap))
    assert r.returncode == 0
    lines = {l.split("|")[1].strip(): l for l in r.stdout.splitlines()
             if l.startswith("| ") and "voice" not in l}
    assert "✅" in lines["PASS"]
    assert "❌" in lines["LOW_NAT"]   # mean nat 3.0 < 3.5
    assert "❌" in lines["LOW_INT"]   # mean int 3.0 < 4.0
    assert lines["PASS"].count("|") and "4.00" in lines["PASS"]


def test_audition_bilingual_pass_assigned_to_one_language(tmp_path):
    snap = snap_file(tmp_path, [
        {"id": "BOTH", "description": "Nam · Bắc · kể chuyện", "featured": True},
        {"id": "VI_ONLY", "description": "Nữ · Trung · tin tức", "featured": False},
        {"id": "VI_3", "description": "Nam · Nam · tự nhiên", "featured": False},
        {"id": "VI_4", "description": "Nữ · Bắc · đọc truyện", "featured": False},
        {"id": "EN_ONLY", "description": "Nữ · Nam · kể chuyện", "featured": False},
    ])

    def row(lang, sid, vid, nat, int_, fit):
        return [lang, sid, vid, "", "", "", "R", str(nat), str(int_), str(fit),
                "", "", ""]

    res = _results(tmp_path, [
        row("vi", "s-1", "BOTH", 5, 5, 5),
        row("vi", "s-2", "VI_ONLY", 5, 5, 4),
        row("vi", "s-3", "VI_3", 4, 5, 4),
        row("vi", "s-4", "VI_4", 4, 5, 3),
        row("en", "s-5", "BOTH", 5, 5, 5),
        row("en", "s-6", "EN_ONLY", 5, 5, 4),
    ])
    r = run("summarize", "--results", str(res), "--snapshot", str(snap))
    assert r.returncode == 0
    sugg = r.stdout.split("Suggested selection")[1]
    en_lines = [l for l in sugg.splitlines() if l.startswith("| en")]
    vi_lines = [l for l in sugg.splitlines() if l.startswith("| vi")]
    # BOTH passes in both languages; en has only 2 passing (< 3) → en keeps it.
    assert any("BOTH" in l for l in en_lines)
    assert not any("BOTH" in l for l in vi_lines)


def test_audition_opener_ignores_env_proxy():
    """SecRev P1: the VieNeu key must never reach a configured proxy."""
    code = (
        "import importlib.util, urllib.request, sys\n"
        f"spec = importlib.util.spec_from_file_location('audition', r'{TOOL}')\n"
        "m = importlib.util.module_from_spec(spec)\n"
        "sys.modules['audition'] = m\n"
        "spec.loader.exec_module(m)\n"
        "assert m._PROXY_HANDLER.proxies == {}, m._PROXY_HANDLER.proxies\n"
        "assert all(getattr(h, 'proxies', None) in (None, {})\n"
        "           for h in m._OPENER.handlers)\n"
        # sanity: a naive opener in this same env DOES see the proxy
        "d = urllib.request.build_opener()\n"
        "assert any(isinstance(h, urllib.request.ProxyHandler)\n"
        "           and h.proxies.get('http') for h in d.handlers)\n"
    )
    env = dict(os.environ)
    env["HTTP_PROXY"] = "http://proxy.invalid:3128"
    env["http_proxy"] = "http://proxy.invalid:3128"
    r = subprocess.run([sys.executable, "-c", code],
                       capture_output=True, text=True, env=env)
    assert r.returncode == 0, r.stderr


def test_audition_ffmpeg_child_env_has_no_vieneu_key(tmp_path, monkeypatch):
    """D13.1 (04 README): the ffmpeg child must not inherit VIENEU_API_KEY."""
    secret = "vk-vieneu-" + "B" * 40
    dump = tmp_path / "ffmpeg.env"
    stub = tmp_path / "ffmpeg-stub"
    stub.write_text(
        "#!/bin/sh\n"
        f"env > {shlex.quote(str(dump))}\n"
        "cat >/dev/null\n"
        "exit 0\n",
        encoding="utf-8",
    )
    stub.chmod(0o755)
    monkeypatch.setenv("VIENEU_API_KEY", secret)
    monkeypatch.setenv("NTS_LEAK_MARKER", "must-not-travel")
    monkeypatch.setenv("FFMPEG_PATH", str(stub))

    spec = importlib.util.spec_from_file_location("audition", TOOL)
    mod = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(mod)

    proc = mod._start_ffmpeg(tmp_path / "sample.mp3")
    try:
        proc.stdin.close()
        proc.wait()
    finally:
        if proc.poll() is None:
            proc.kill()
            proc.wait()

    child = dump.read_text(encoding="utf-8")
    assert "PATH=" in child  # the stub ran and dumped a real environment
    assert "VIENEU_API_KEY" not in child
    assert secret not in child
    assert "NTS_LEAK_MARKER" not in child


def test_audition_allowlist_matches_the_proxy_allowlist():
    """D13.1 (04 README): the standalone tool may not drift from the proxy's list."""
    from nts_vieneu_proxy import transcode

    spec = importlib.util.spec_from_file_location("audition", TOOL)
    mod = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(mod)
    assert tuple(mod.CHILD_ENV_KEYS) == tuple(transcode._CHILD_ENV_KEYS)  # noqa: SLF001


def test_audition_missing_ffmpeg_exits_2_before_any_synthesis(tmp_path):
    """QA-PR7-4: a missing encoder fails cleanly (exit 2), with no traceback."""
    snap = snap_file(tmp_path, [
        {"id": "Adam", "description": "Nam · Bắc · kể chuyện"},
    ])
    r = run("generate", "--snapshot", str(snap), "--lang", "vi",
            "--out", str(tmp_path),
            env_extra={"VIENEU_API_KEY": "vk-not-a-real-key",
                       "FFMPEG_PATH": str(tmp_path / "no-such-ffmpeg")})
    assert r.returncode == 2, r.stderr
    assert "ffmpeg not found" in r.stderr
    assert "Traceback" not in r.stderr
    assert not list(tmp_path.glob("*.mp3"))
