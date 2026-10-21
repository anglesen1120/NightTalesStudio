"""Freeze / verify `prompts/manifest.json` (02.1.3-T2 §1).

Keys are relative to `prompts/` (T27), each value is `{"version", "sha256"}`
where `version` is the file's own marker (`*.v1.md` → `v1`) and `sha256` is the
digest of the raw bytes (no line-ending normalisation — the catalog hashes the
embedded resource).

Excluded from the release manifest (T27 / 02 T75): `fixtures/**`, `tools/**`,
`manifest.json`, `REFERENCES.md`, `**/.gitkeep` and dotfiles.

Default / `--freeze` writes the manifest; `--check` recomputes and compares.
Exit codes (01 D9): 0 ok · 1 a check failed (a listed file's hash differs, a
listed file is missing, or a non-excluded `prompts/**` file is unlisted) · 2
usage or environment error (an unversioned prompt file, unreadable tree).

Stdlib only: `uv run --no-project --python 3.12 trial/tools/freeze_manifest.py`.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import re
import sys
import tempfile
from pathlib import Path

VERSION_RE = re.compile(r"\.(v\d+)\.[^.]+$")
DEFAULT_EXCLUDES = {"manifest.json", "REFERENCES.md"}


def _dotfile(rel: str) -> bool:
    return any(part.startswith(".") for part in Path(rel).parts)


def _excluded(rel: str, extra: list[str]) -> bool:
    if _dotfile(rel):
        return True
    if rel in DEFAULT_EXCLUDES:
        return True
    first = rel.split("/", 1)[0]
    if first in {"fixtures", "tools"}:
        return True
    if rel.endswith(".gitkeep") or Path(rel).name == ".gitkeep":
        return True
    return rel in extra


def version_of(rel: str) -> str | None:
    match = VERSION_RE.search(Path(rel).name)
    return match.group(1) if match else None


def file_digest(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


def scan_files(prompts_dir: Path, extra: list[str]) -> tuple[list[str], str | None]:
    """All releasable `prompts/` files; error string on an unversioned file."""
    found = []
    for path in sorted(prompts_dir.rglob("*")):
        if not path.is_file():
            continue
        rel = path.relative_to(prompts_dir).as_posix()
        if _excluded(rel, extra):
            continue
        if version_of(rel) is None:
            return [], f"unversioned prompt file: {rel} (need a *.vN.* marker)"
        found.append(rel)
    return found, None


def build_manifest(prompts_dir: Path, release: str, extra: list[str]) -> tuple[dict, str | None]:
    files, error = scan_files(prompts_dir, extra)
    if error:
        return {}, error
    entries = {}
    for rel in files:
        entries[rel] = {"version": version_of(rel), "sha256": file_digest(prompts_dir / rel)}
    return {"release": release, "files": entries}, None


def check(prompts_dir: Path, extra: list[str]) -> tuple[int, list[str]]:
    """Verify the manifest. Returns (0 ok | 1 failed | 2 error, messages)."""
    manifest_path = prompts_dir / "manifest.json"
    if not manifest_path.is_file():
        return 2, ["prompts/manifest.json is missing — freeze first"]
    try:
        manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
        listed = {k: v for k, v in manifest.get("files", {}).items()}
    except (ValueError, AttributeError, OSError) as exc:
        return 2, [f"prompts/manifest.json unreadable: {exc}"]

    files, error = scan_files(prompts_dir, extra)
    if error:
        return 2, [error]
    problems = []
    for rel, entry in sorted(listed.items()):
        target = prompts_dir / rel
        if not target.is_file():
            problems.append(f"missing file listed in manifest: {rel}")
        elif file_digest(target) != entry.get("sha256"):
            problems.append(f"sha256 mismatch: {rel}")
        elif entry.get("version") != version_of(rel):
            problems.append(f"version mismatch: {rel}")
    unlisted = sorted(set(files) - set(listed))
    for rel in unlisted:
        problems.append(f"unlisted prompts file: {rel} (added but never released)")
    return (1 if problems else 0), problems


def freeze(prompts_dir: Path, release: str, extra: list[str]) -> int:
    manifest, error = build_manifest(prompts_dir, release, extra)
    if error:
        print(f"error: {error}", file=sys.stderr)
        return 2
    (prompts_dir / "manifest.json").write_text(
        json.dumps(manifest, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
    print(f"froze {len(manifest['files'])} files at release {release} -> prompts/manifest.json")
    return 0


def selftest() -> int:
    with tempfile.TemporaryDirectory() as tmp:
        prompts_dir = Path(tmp) / "prompts"
        (prompts_dir / "story").mkdir(parents=True)
        (prompts_dir / "system").mkdir(parents=True)
        (prompts_dir / "fixtures").mkdir(parents=True)
        (prompts_dir / "story" / "ghost.vi.v1.md").write_text("ghost preset\n")
        (prompts_dir / "system" / "writer.v1.md").write_text("writer\n")
        (prompts_dir / "fixtures" / "rubric.md").write_text("rubric\n")
        (prompts_dir / ".gitkeep").write_text("")
        (prompts_dir / "REFERENCES.md").write_text("provenance\n")

        def run(mode, *extra):
            code = main(["--prompts-dir", str(prompts_dir)] + list(mode))
            return code

        assert run(("--check",)) == 2, "no manifest must be exit 2"
        assert run(("--freeze",)) == 0
        assert run(("--check",)) == 0, "frozen tree must verify"
        manifest = json.loads((prompts_dir / "manifest.json").read_text())
        assert set(manifest["files"]) == {"story/ghost.vi.v1.md", "system/writer.v1.md"}, \
            "fixtures/.gitkeep/REFERENCES.md must stay out"

        # mutation -> 1 naming the file
        (prompts_dir / "story" / "ghost.vi.v1.md").write_text("ghost preset edited\n")
        assert run(("--check",)) == 1, "mutation must fail"
        (prompts_dir / "story" / "ghost.vi.v1.md").write_text("ghost preset\n")

        # missing listed file -> 1
        (prompts_dir / "system" / "writer.v1.md").unlink()
        assert run(("--check",)) == 1, "missing listed file must fail"
        (prompts_dir / "system" / "writer.v1.md").write_text("writer\n")

        # unlisted versioned file -> 1
        (prompts_dir / "story" / "new.en.v1.md").write_text("new preset\n")
        assert run(("--check",)) == 1, "unlisted versioned file must fail"

        # excluded additions -> still 0
        (prompts_dir / "fixtures" / "new.json").write_text("{}\n")
        (prompts_dir / "story" / "new.en.v1.md").unlink()
        assert run(("--check",)) == 0, "a fixtures/** addition must not trip the check"

        # unversioned prompt file -> 2
        (prompts_dir / "system" / "writer.md").write_text("unversioned\n")
        assert run(("--check",)) == 2, "unversioned file must be exit 2"
        (prompts_dir / "system" / "writer.md").unlink()
    print("selftest ok")
    return 0


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(
        description="Freeze or verify prompts/manifest.json (02.1.3-T2).")
    parser.add_argument("--prompts-dir", default="prompts", help="prompts tree (default ./prompts)")
    parser.add_argument("--release", default="2026.10.1", help="release string (default 2026.10.1)")
    parser.add_argument("--exclude", action="append", default=[],
                        help="extra prompts-relative path to exclude (repeatable)")
    parser.add_argument("--check", action="store_true",
                        help="recompute and compare without writing")
    parser.add_argument("--freeze", action="store_true",
                        help="rewrite the manifest (also the default when neither flag is given)")
    parser.add_argument("--selftest", action="store_true", help="run the offline self-test")
    args = parser.parse_args(argv)

    if args.selftest:
        return selftest()

    prompts_dir = Path(args.prompts_dir)
    if not prompts_dir.is_dir():
        print(f"error: --prompts-dir {prompts_dir} does not exist", file=sys.stderr)
        return 2
    if args.check:
        code, messages = check(prompts_dir, args.exclude)
        for message in messages:
            print(f"check failed: {message}")
        return code
    return freeze(prompts_dir, args.release, args.exclude)


if __name__ == "__main__":
    sys.exit(main())
