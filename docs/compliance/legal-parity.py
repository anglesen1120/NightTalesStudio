#!/usr/bin/env python3
"""Legal-content parity check for 13.1.1-T1 (privacy) and 13.1.2-T1 (terms).

Repeatable, file-level verification of the four drafts under `web/src/content/legal/` and the
`manifest.json` they are versioned by. Stdlib only; no repository test suite is involved (the
build-time enforcement is `tools/ci/check-legal-versions.py`, 13.2.2-T1, and `legal-to-json.mjs`,
10.3.1-T1).

Checks, per document (`privacy` and `tos`):
  1. the manifest carries the document at a semver-like version and an ISO `effectiveDate`;
  2. both locales exist as exactly `<prefix>.<version>.<lang>.md` under `web/src/content/legal/`
     (the file-prefix map is `tos -> terms`, `privacy -> privacy`) and the two locale file names
     carry the *same* version, equal to the manifest version (a `vi`/`en` version divergence fails);
     the legal directory, `manifest.json` and each document are rejected before they are read if
     any path component under the supplied root is a symlink, or if the real path escapes the root;
  3. one `#` title, no front matter and no HTML comment;
  4. the numbered `## N.` heading sets are identical in both locales and are exactly `1..N`;
  5. the required links sit *inside their owning slot* (privacy slot 3: the YouTube Terms and the
     Google Privacy Policy; privacy slot 4: the Google permissions page), privacy slot 3 states
     that the service uses YouTube API Services, privacy slot 7 carries the never-ask-for-a-
     password statement, and `tos` slot 8 carries both the YouTube Terms link and the III.A.1
     binding sentence;
  6. the Markdown subset the build-time converter accepts, as a per-line allow-list: only the `# `
     title, `## N. ` / `### ` headings, top-level `- ` / `1. ` list items, `> ` quotes, paragraph
     lines and blank lines are accepted; a quote or list remainder is re-classified after its
     marker is stripped, so `> - x`, `- - x` and `> ~~~js` fail, and the only ordered marker is
     `1. `. Fenced or indented code, `####`+, a Setext underline, a `---`/`***`/`___` rule, a table
     row, an image, a footnote, a reference-style definition, raw HTML or an autolink, and a bare
     URL are rejected. Every `](target)` is parsed strictly: `https://` must carry a host of
     `[A-Za-z0-9.-]+` with a dot, `mailto:` a non-empty recipient before any `?`, and a `/`-rooted
     path is the only other form — an unclosed, spaced, empty, backslashed, protocol-relative
     (`//host`) or other-scheme target fails;
  7. neither file hardcodes a version literal or an effective date in the prose (every file read is
     capped at 256 KiB, so an oversized draft fails instead of being parsed);
  8. `privacy` cites 06's `pc.v1` consent text by path (13 README D2);
  9. `tos` does not restate the retention windows owned by the privacy policy / plan 13 §5.4;
 10. the per-slot block structure (paragraphs and top-level bullets) is identical in both locales, and the
     bullet counts the acceptance checks name for a slot are met (this is the structural part of the
     translation-drift guard; reading the pair for meaning stays a review step).

Usage:  python3 docs/compliance/legal-parity.py [root]   (-h/--help prints this text, exit 0)
Exit:   0 = every check passed (prints one `OK <doc> <version> <n>/<n>` line per document)
        1 = a named check failed
        2 = usage error (more than one argument)
"""

from __future__ import annotations

import json
import re
import sys
from pathlib import Path
from urllib.parse import urlsplit

USAGE = "usage: legal-parity.py [root]"

# doc (manifest key) -> (file-name prefix, numbered `##` slots fixed by the task file)
# 13.1.2-T1 §Design names the files `terms.<version>.<lang>.md` while the manifest key is `tos` (K1/K2).
DOCS: dict[str, tuple[str, int]] = {"privacy": ("privacy", 10), "tos": ("terms", 12)}

YOUTUBE_TOS = "https://www.youtube.com/t/terms"
GOOGLE_PRIVACY = "https://policies.google.com/privacy"
GOOGLE_PERMISSIONS = "https://security.google.com/settings/security/permissions"

REQUIRED_LINKS: dict[str, tuple[str, ...]] = {
    "privacy": (YOUTUBE_TOS, GOOGLE_PRIVACY, GOOGLE_PERMISSIONS),
    "tos": (YOUTUBE_TOS,),
}

# III.A.1: a bare link is not enough — the sentence must bind the user to the YouTube ToS.
BINDING = {"en": "bound by", "vi": "ràng buộc"}

# QA-13-06 / III.A.2: the YouTube API Services statement and the never-ask-for-a-password sentence
# must sit in their own slot, in the locale's own wording.
YOUTUBE_API_SERVICES = {
    "en": "NightTalesStudio uses YouTube API Services",
    "vi": "NightTalesStudio sử dụng YouTube API Services",
}
NEVER_ASK_PASSWORD = {
    "en": "We never ask you for your YouTube, Google or TikTok password",
    "vi": "Chúng tôi không bao giờ hỏi mật khẩu YouTube, Google hay TikTok của bạn",
}

# QA-13-06: a required link is only accepted inside the slot that owns it, never elsewhere.
REQUIRED_LINKS_BY_SLOT: dict[str, dict[str, tuple[str, ...]]] = {
    "privacy": {"3": (YOUTUBE_TOS, GOOGLE_PRIVACY), "4": (GOOGLE_PERMISSIONS,)},
}

# 13.1.2-T1 §Tests: the retention windows live in the privacy policy only.
RETENTION_WINDOWS = re.compile(r"\b(7|30) (days|ngày)\b")

# Bullet counts fixed by the task files' acceptance checks (slot -> top-level bullets).
EXPECTED_BULLETS: dict[str, dict[str, int]] = {
    "privacy": {"5": 6},  # the six retention/deletion bullets, never a table
    "tos": {"4": 4},      # credits, expiry and refunds (12.6.2 / FR-12-14)
}

# The heading forms the build-time converter accepts. Quotes and list items are handled (and
# re-classified after their marker is stripped) inside `block_violation`.
BLOCK_ALLOW = re.compile(r"(?:# \S|## \d+\. \S|### \S)")

# Inline constructs that are rejected wherever they appear in the text.
REJECT_PATTERNS: tuple[tuple[str, re.Pattern[str]], ...] = (
    # Every CommonMark raw-HTML form plus the autolink: `<tag`, `</tag`, `<!…>`, `<?…>`, `<scheme:…>`.
    ("raw HTML or autolink", re.compile(r"<[a-zA-Z/!?]")),
    ("image", re.compile(r"!\[")),
    ("footnote", re.compile(r"\[\^")),
    ("reference-style link definition", re.compile(r"^ {0,3}\[[^\]\n]+\]:", re.MULTILINE)),
)

LINK_OPEN = re.compile(r"\]\(")
LINK_TARGET = re.compile(r"\]\(https://[^\s)]+\)")

# Every file read is capped, so a runaway blob cannot stall the check.
MAX_FILE_BYTES = 256 * 1024

VERSION_LITERAL = re.compile(r"\b\d+\.\d+\.\d+\b")
ISO_DATE = re.compile(r"\b\d{4}-\d{2}-\d{2}\b")
SEMVER = re.compile(r"^\d+\.\d+\.\d+$")

PC_V1_PATHS = ("Templates/Consent/pc.v1.vi.md", "Templates/Consent/pc.v1.en.md")


class Failure(Exception):
    """A named check failure; the message is printed as `FAIL: <message>`."""


def check(condition: bool, message: str) -> None:
    if not condition:
        raise Failure(message)


def heading_numbers(text: str) -> list[str]:
    return [m.group(1) for m in re.finditer(r"^## ([0-9]+)\.", text, re.MULTILINE)]


def slot_bodies(text: str) -> dict[str, str]:
    """Slot number -> the body between its `## N. ` heading and the next one."""
    parts = re.split(r"^## (\d+)\. ", text, flags=re.MULTILINE)
    return {parts[i]: parts[i + 1] for i in range(1, len(parts), 2)}


def slot_shapes(text: str) -> dict[str, tuple[int, int]]:
    """Slot number -> (paragraph blocks, top-level bullets)."""
    shapes: dict[str, tuple[int, int]] = {}
    for slot, body in slot_bodies(text).items():
        paragraphs = [
            b for b in body.strip().split("\n\n") if b.strip() and not b.strip().startswith("- ")
        ]
        bullets = [line for line in body.split("\n") if line.startswith("- ")]
        shapes[slot] = (len(paragraphs), len(bullets))
    return shapes


def destination_scheme(destination: str) -> str:
    """The scheme of a link destination, for a message that names it."""
    match = re.match(r"([A-Za-z][A-Za-z0-9+.-]*):", destination)
    return match.group(1).lower() if match else "(none)"


def block_violation(line: str, nested: bool = False) -> str | None:
    """Name the block form a line breaks, or None when the line is an accepted block.

    Accepted: a blank line, the `# ` title, a `## N. ` or `### ` heading, a top-level `- ` or
    `1. ` list item, a `> ` quote, or a paragraph line. A quote or a list item is re-classified
    after its marker is stripped, so a nested list item (`> - x`, `- - x`) and a fence or rule
    hidden inside a quote (`> ~~~js`) are rejected. Everything else — fenced or indented code,
    any other ATX heading (`####` and up, or an unnumbered `##`), a Setext underline, a
    `---`/`***`/`___` rule, a table row, a reference-style definition, any other list marker
    (`*`/`+`/`_`, or an ordered marker above `1. `), a leading-space line — is rejected.
    """
    if not line.strip():
        return None
    if line[0] in " \t":
        return "indented block or nested list item"
    if line.startswith("> "):
        return block_violation(line[2:], nested=True)
    if line.startswith("- "):
        return "nested list item" if nested else block_violation(line[2:], nested=True)
    if line.startswith("1. "):
        return "nested list item" if nested else block_violation(line[3:], nested=True)
    if BLOCK_ALLOW.match(line):
        return None
    first = line[0]
    if line.startswith(("```", "~~~")):
        return "fenced code block"
    if first == "#":
        return f"heading not in the subset ({line.split()[0]!r})"
    if first == ">":
        return "block quote not in the subset"
    if re.match(r"([-*_])\1{2,}\s*$", line) or re.match(r"[=-]+\s*$", line):
        return "thematic break or Setext underline"
    if first == "|":
        return "table row"
    if re.match(r"\[[^\]]+\]:", line):
        return "reference-style link definition"
    if re.match(r"[*+_]\s", line):
        return "list marker not in the subset (only '- ' is accepted)"
    if re.match(r"\d+\.\s", line):
        return "ordered list marker not in the subset (only '1. ' is accepted)"
    return None


def link_violation(text: str) -> str | None:
    """Name the first link target that is not one of the three strict allowed forms.

    Allowed: `https://<host>…` (`urlsplit` must yield a hostname of `[A-Za-z0-9.-]+` with a dot,
    no backslash), `mailto:<recipient>[?…]` (a non-empty recipient before any `?`), or a
    `/`-rooted site path. An unclosed, spaced, empty or ambiguous target is rejected, as is a
    protocol-relative `//host` target.
    """
    for match in LINK_OPEN.finditer(text):
        end = text.find(")", match.end())
        if end == -1:
            return f"unclosed link target after offset {match.start()}"
        target = text[match.end() : end]
        if not target or target != target.strip() or re.search(r"[\s()<>\"'\\]", target):
            return f"missing, spaced or ambiguous link target {target!r}"
        if target.startswith("//"):
            return f"protocol-relative link target {target!r}"
        if target.startswith("https://"):
            host = urlsplit(target).hostname or ""
            if re.fullmatch(r"[A-Za-z0-9.-]+", host) is None or "." not in host:
                return f"https link without a valid host {target!r}"
        elif target.startswith("mailto:"):
            recipient = target[len("mailto:") :].split("?", 1)[0]
            if not recipient or "//" in target:
                return f"malformed mailto: target {target!r}"
        elif not target.startswith("/"):
            return f"link target {target!r} uses scheme {destination_scheme(target)!r}"
    return None


def read(path: Path) -> str:
    try:
        size = path.stat().st_size
        if size > MAX_FILE_BYTES:
            raise Failure(f"{path.name}: {size} bytes exceeds the {MAX_FILE_BYTES}-byte cap")
        return path.read_text(encoding="utf-8")
    except OSError as exc:
        raise Failure(f"cannot read {path}: {exc}") from exc


def within(root: Path, path: Path) -> Path:
    """Resolve `path` under `root`, rejecting a symlink at any component and any escape.

    Every component from `root` down to `path` must not be a symlink, and the resolved path must
    stay inside the resolved `root` (SEC review: only the expected files may be read).
    """
    try:
        parts = path.relative_to(root).parts
    except ValueError:
        raise Failure(f"{path} is not under {root}") from None
    real_root = root.resolve()
    current = root
    for part in parts:
        current = current / part
        check(not current.is_symlink(), f"{current} is a symlink, which is not accepted")
    real = path.resolve()
    check(real == real_root or real_root in real.parents, f"{path} resolves outside {real_root}")
    return real


def locale_file(legal_dir: Path, prefix: str, lang: str) -> tuple[Path, str]:
    """Return the single exact `<prefix>.<version>.<lang>.md` file of a document and its version.

    Only the expected file names are considered, and `within` rejects a symlink (or a file that
    resolves outside `legal_dir`) before it can be read.
    """
    pattern = re.compile(rf"{re.escape(prefix)}\.(\d+\.\d+\.\d+)\.{re.escape(lang)}\.md")
    matches = sorted(
        p for p in legal_dir.iterdir() if p.is_file() and pattern.fullmatch(p.name)
    )
    check(
        len(matches) == 1,
        f"expected exactly one {prefix}.<version>.{lang}.md, found "
        f"{sorted(p.name for p in legal_dir.iterdir() if p.name.startswith(f'{prefix}.') and p.name.endswith(f'.{lang}.md'))}",
    )
    path = matches[0]
    check(not path.is_symlink(), f"{path.name}: a symlinked legal text is not accepted")
    within(legal_dir, path)
    version = path.name[len(prefix) + 1 : -(len(lang) + 4)]
    return path, version


def check_manifest(manifest: dict) -> None:
    check(
        sorted(manifest) == ["privacy", "tos"],
        f"manifest keys must be exactly ['privacy', 'tos'], found {sorted(manifest)}",
    )
    for doc in DOCS:
        entry = manifest[doc]
        check(isinstance(entry, dict), f"manifest[{doc!r}] must be an object")
        check(
            sorted(entry) == ["effectiveDate", "version"],
            f"manifest[{doc!r}] keys must be version + effectiveDate, found {sorted(entry)}",
        )
        check(
            SEMVER.match(str(entry["version"])) is not None,
            f"manifest[{doc!r}].version {entry['version']!r} is not semver-like",
        )
        check(
            ISO_DATE.fullmatch(str(entry["effectiveDate"])) is not None,
            f"manifest[{doc!r}].effectiveDate {entry['effectiveDate']!r} is not an ISO date",
        )


def check_doc(legal_dir: Path, manifest: dict, doc: str) -> str:
    prefix, slots = DOCS[doc]
    manifest_version = str(manifest[doc]["version"])
    files: dict[str, Path] = {}
    versions: dict[str, str] = {}
    for lang in ("vi", "en"):
        files[lang], versions[lang] = locale_file(legal_dir, prefix, lang)

    check(
        versions["vi"] == versions["en"],
        f"{doc}: locale versions diverge, vi={versions['vi']} en={versions['en']}",
    )
    check(
        versions["vi"] == manifest_version,
        f"{doc}: file version {versions['vi']} != manifest version {manifest_version}",
    )

    texts = {lang: read(path) for lang, path in files.items()}

    for lang, text in texts.items():
        name = files[lang].name
        check(text.startswith("# "), f"{name}: must start with a single '# ' title")
        check(not text.startswith("---"), f"{name}: front matter is not in the subset")
        check("<!--" not in text, f"{name}: an HTML comment is not in the subset")
        check(
            text.count("\n# ") == 0,
            f"{name}: more than one '# ' title",
        )
        check(
            heading_numbers(text) == [str(i) for i in range(1, slots + 1)],
            f"{name}: numbered '## N.' slots must be 1..{slots} in order, "
            f"found {heading_numbers(text)}",
        )
        for number, line in enumerate(text.splitlines(), start=1):
            broken = block_violation(line)
            check(broken is None, f"{name}: {broken} on line {number}")
        for label, pattern in REJECT_PATTERNS:
            hit = pattern.search(text)
            check(hit is None, f"{name}: {label} at offset {hit.start() if hit else -1}")
        broken = link_violation(text)
        check(broken is None, f"{name}: {broken}")
        stripped = LINK_TARGET.sub("](LINK)", text)
        hit = re.search(r"https?://", stripped)
        check(hit is None, f"{name}: bare URL at offset {hit.start() if hit else -1}")
        for label, pattern in (
            ("version literal", VERSION_LITERAL),
            ("effective date", ISO_DATE),
        ):
            hit = pattern.search(text)
            check(
                hit is None,
                f"{name}: {label} {hit.group(0)!r} in the prose" if hit else "",
            )
        for url in REQUIRED_LINKS[doc]:
            check(url in text, f"{name}: missing required link {url}")
        bodies = slot_bodies(text)
        for slot, urls in REQUIRED_LINKS_BY_SLOT.get(doc, {}).items():
            for url in urls:
                check(
                    url in bodies.get(slot, ""),
                    f"{name}: slot {slot} must contain the link {url}",
                )

    shapes = {lang: slot_shapes(text) for lang, text in texts.items()}
    check(
        shapes["vi"] == shapes["en"],
        f"{doc}: per-slot (paragraphs, bullets) differ, vi={shapes['vi']} en={shapes['en']}",
    )
    for slot, bullets in EXPECTED_BULLETS.get(doc, {}).items():
        for lang, shape in shapes.items():
            check(
                shape[slot][1] == bullets,
                f"{files[lang].name}: slot {slot} must hold {bullets} bullets, "
                f"found {shape[slot][1]}",
            )

    if doc == "privacy":
        for lang, text in texts.items():
            name = files[lang].name
            bodies = slot_bodies(text)
            check(
                YOUTUBE_API_SERVICES[lang] in bodies.get("3", ""),
                f"{name}: slot 3 must state that the service uses YouTube API Services "
                f"({YOUTUBE_API_SERVICES[lang]!r})",
            )
            check(
                NEVER_ASK_PASSWORD[lang] in bodies.get("7", ""),
                f"{name}: slot 7 must carry the never-ask-for-a-password statement "
                f"({NEVER_ASK_PASSWORD[lang]!r})",
            )
            check("pc.v1" in text, f"{name}: does not cite pc.v1 (13 README D2)")
            for rel in PC_V1_PATHS:
                check(rel in text, f"{name}: does not point at {rel}")

    if doc == "tos":
        for lang, text in texts.items():
            name = files[lang].name
            slot8 = slot_bodies(text).get("8", "")
            check(
                YOUTUBE_TOS in slot8,
                f"{name}: slot 8 must link the YouTube Terms of Service ({YOUTUBE_TOS})",
            )
            check(
                BINDING[lang] in slot8,
                f"{name}: slot 8 has no III.A.1 binding sentence ({BINDING[lang]!r})",
            )
            hit = RETENTION_WINDOWS.search(text)
            check(
                hit is None,
                f"{name}: restates the retention window {hit.group(0)!r} owned by the privacy policy"
                if hit
                else "",
            )

    return f"OK {doc} {versions['en']} {slots}/{slots}"


def main(argv: list[str]) -> int:
    if len(argv) > 2:
        print(USAGE)
        return 2
    if len(argv) == 2 and argv[1] in ("-h", "--help"):
        print(__doc__.strip())
        return 0
    root = Path(argv[1] if len(argv) == 2 else ".").resolve()
    legal_dir = root / "web" / "src" / "content" / "legal"
    try:
        check(legal_dir.is_dir(), f"missing directory {legal_dir}")
        within(root, legal_dir)
        manifest_path = legal_dir / "manifest.json"
        check(manifest_path.is_file(), f"missing {manifest_path}")
        within(root, manifest_path)
        try:
            manifest = json.loads(read(manifest_path))
        except json.JSONDecodeError as exc:
            raise Failure(f"{manifest_path.name} is not valid JSON: {exc}") from exc
        check(isinstance(manifest, dict), "manifest.json must be an object")
        check_manifest(manifest)
        for doc in DOCS:
            print(check_doc(legal_dir, manifest, doc))
    except Failure as exc:
        print(f"FAIL: {exc}")
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
