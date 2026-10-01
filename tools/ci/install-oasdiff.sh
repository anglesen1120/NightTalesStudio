#!/usr/bin/env bash
#
# Install the pinned oasdiff binary for the `contract` job of 09.9.5-T1 (09 README W11: oasdiff,
# Apache-2.0, "detect breaking changes in OpenAPI specs").
#
# The version and the SHA-256 of every archive this script may download are constants below, so the
# gate always diffs with a known binary and a tampered, truncated or re-released download fails the
# job. There is no `latest` URL and no "already installed, skip" path: a failure to install is a
# failure of the gate, never a silent pass.
#
# Usage:
#   tools/ci/install-oasdiff.sh [install-dir]     # default: ./bin (git-ignored)
#
# The binary is written to <install-dir>/oasdiff and the path is printed. The `contract` job adds
# <install-dir> to $GITHUB_PATH so the later steps find `oasdiff` on the PATH.
#
# Exit codes: 0 = installed and verified, 1 = unsupported platform or a failed download/verification.
set -euo pipefail

readonly version="1.32.1"
readonly project="oasdiff/oasdiff"

# The archive a platform needs. `darwin` ships one universal archive; Linux has one per
# architecture. Only the platforms the gate and a developer on macOS need are listed.
archive_for() {
  case "$1" in
    linux_x86_64) echo "oasdiff_${version}_linux_amd64.tar.gz" ;;
    linux_aarch64) echo "oasdiff_${version}_linux_arm64.tar.gz" ;;
    darwin_*) echo "oasdiff_${version}_darwin_all.tar.gz" ;;
    *) return 1 ;;
  esac
}

# The SHA-256 of each archive above, taken from the release assets. A bump of `version` without a bump
# here fails closed: the download is verified, never trusted.
sha_for() {
  case "$1" in
    "oasdiff_${version}_linux_amd64.tar.gz") echo "7c8939fc49b75ee11fec66a5b83b37a2fca6aee109fed85013b1ba2ac2a1ee7f" ;;
    "oasdiff_${version}_linux_arm64.tar.gz") echo "32fff58a120f75a723d6c2422444691c37fa6813fed61d23f53dbcb604b30f6d" ;;
    "oasdiff_${version}_darwin_all.tar.gz") echo "e4d74b7e2dfb9d4819e7fc720c905ec86547e4637ac270a2b0187c0f1fb7187e" ;;
    *) return 1 ;;
  esac
}

# `sha256sum` on Linux, `shasum` on macOS: both are always present on their platform.
sha256() {
  if command -v sha256sum >/dev/null 2>&1; then
    sha256sum "$1" | awk '{print $1}'
  else
    shasum -a 256 "$1" | awk '{print $1}'
  fi
}

dest="${1:-bin}"
platform="$(uname -s | tr '[:upper:]' '[:lower:]')_$(uname -m)"

if ! archive="$(archive_for "$platform")"; then
  echo "install-oasdiff: unsupported platform '${platform}'" >&2
  exit 1
fi

if ! expected="$(sha_for "$archive")"; then
  echo "install-oasdiff: no SHA-256 for '${archive}': bump the hash with the version" >&2
  exit 1
fi

url="https://github.com/${project}/releases/download/v${version}/${archive}"
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT

echo "install-oasdiff: downloading ${url}"
curl --fail --silent --show-error --location --output "$work/$archive" "$url"

actual="$(sha256 "$work/$archive")"
if [ "$actual" != "$expected" ]; then
  echo "install-oasdiff: SHA-256 mismatch for ${archive}" >&2
  echo "  expected ${expected}" >&2
  echo "  actual   ${actual}" >&2
  exit 1
fi

# The archive holds LICENSE and the binary; only the binary is installed.
tar -xzf "$work/$archive" -C "$work" oasdiff
mkdir -p "$dest"
install -m 0755 "$work/oasdiff" "$dest/oasdiff"

echo "install-oasdiff: installed $("$dest/oasdiff" --version) at ${dest}/oasdiff"
