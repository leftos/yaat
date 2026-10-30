#!/usr/bin/env bash
# Builds a client release's notes from CHANGELOG.md. Shared by release.yml and the local
# release route (release.ps1), so a hosted and a local release carry the same body.
#
#   release-notes.sh notes <tag> <owner/repo> <out-dir>
#       Writes <out-dir>/changelog.md, highlights.md and showcase.md from the CHANGELOG.md
#       section of <tag> (run from the repo root).
#   release-notes.sh body <version> <notes-dir>
#       Prints the release body built from those three files.
set -euo pipefail

notes() {
  local current_tag="$1" repository="$2" out_dir="$3"
  # Headings look like "## 0.1.1-alpha" or "## 0.1.1-alpha - 2026-04-24".
  # The tag is "v0.1.1-alpha"; strip the leading "v" before matching.
  local version="${current_tag#v}"

  if [[ ! -f CHANGELOG.md ]]; then
    echo "::error::CHANGELOG.md not found at repo root"
    exit 1
  fi

  # Print everything from the first "## " heading containing $version
  # up to (but not including) the next "## " heading. Handles optional
  # date suffix and either "v"-prefixed or bare version headings.
  local full_section
  full_section=$(awk -v ver="$version" '
    /^## / {
      if (in_section) exit
      line = substr($0, 4)
      gsub(/^[ \t]+|[ \t]+$/, "", line)
      # match the version as a whole token (avoid 0.1 matching 0.10)
      if (match(line, "(^|[^0-9.])"ver"([^0-9.]|$)")) {
        in_section = 1
        next
      }
    }
    in_section { print }
  ' CHANGELOG.md)

  if [[ -z "$full_section" ]]; then
    echo "::error::No CHANGELOG.md section found for version '$version'."
    echo "::error::Run the prepare-release skill — it promotes the unreleased section heading to the tagged version."
    exit 1
  fi

  # Split out the "### Highlights" subsection (written by the prepare-release
  # skill) into its own block, and emit the remainder as the changelog body.
  # The release-body builder renders highlights and changelog separately.
  local highlights changelog
  highlights=$(printf '%s\n' "$full_section" | awk '
    /^### / {
      if (in_block) { in_block = 0 }
      if ($0 == "### Highlights") { in_block = 1; next }
    }
    in_block { print }
  ')
  changelog=$(printf '%s\n' "$full_section" | awk '
    /^### / {
      if (in_block) { in_block = 0 }
      if ($0 == "### Highlights") { in_block = 1; next }
    }
    !in_block
  ')

  # Trim leading and trailing blank lines on both blocks.
  trim() { awk 'NF{p=1} p' | sed -e :a -e '/^[[:space:]]*$/{$d;N;ba' -e '}'; }
  highlights=$(printf '%s\n' "$highlights" | trim)
  changelog=$(printf '%s\n' "$changelog" | trim)

  if [[ -z "$changelog" ]]; then
    echo "::error::The CHANGELOG.md section for '$version' has no entries outside ### Highlights."
    exit 1
  fi

  mkdir -p "$out_dir"
  printf '%s\n' "$changelog" > "$out_dir/changelog.md"
  printf '%s\n' "$highlights" > "$out_dir/highlights.md"

  # The release's feature showcase, when it has one, is linked below the
  # highlights. discord-release.yml finds the link by its "**What's new" lead.
  local showcase="docs/releases/whats-new-${version}.md"
  if [[ -f "$showcase" ]]; then
    printf "**What's new in %s, with screenshots:** https://github.com/%s/blob/%s/%s\n" \
      "$version" "$repository" "$current_tag" "$showcase" > "$out_dir/showcase.md"
  else
    : > "$out_dir/showcase.md"
  fi
}

body() {
  local version="$1" notes="$2"
  if [[ ! -s "$notes/changelog.md" ]] || ! grep -q '[^[:space:]]' "$notes/changelog.md"; then
    echo "::error::$notes/changelog.md is missing or empty; refusing to publish a release with an empty Changelog block." >&2
    exit 1
  fi

  if grep -q '[^[:space:]]' "$notes/highlights.md"; then
    echo "## Highlights"
    echo ""
    cat "$notes/highlights.md"
    echo ""
  fi

  if [[ -s "$notes/showcase.md" ]]; then
    cat "$notes/showcase.md"
    echo ""
  fi

  echo "## Changelog"
  echo ""
  cat "$notes/changelog.md"
  echo ""

  echo "---"
  echo ""
  echo "### YAAT"
  echo "| Platform | Installer (recommended) | Portable |"
  echo "|----------|------------------------|----------|"
  echo "| Windows | \`YaatClient-${version}-win-Setup.exe\` | \`YaatClient-${version}-win-Portable.zip\` |"
  echo "| Linux   | \`YaatClient-${version}-linux.AppImage\` | (the AppImage is itself portable) |"
  echo "| macOS (Apple Silicon) | \`YaatClient-${version}-osx-arm64-Setup.pkg\` | \`YaatClient-${version}-osx-arm64-Portable.zip\` |"
  echo "| macOS (Intel) | \`YaatClient-${version}-osx-x64-Setup.pkg\` | \`YaatClient-${version}-osx-x64-Portable.zip\` |"
  echo ""
  echo "Installers include auto-update support. Portable archives unzip to a folder you can run from anywhere — no install, no auto-update. The Linux AppImage is itself portable."
  echo ""
  echo "macOS builds require macOS 14 (Sonoma) or newer. Pick the package matching your Mac — the installer refuses the wrong architecture."
}

case "${1:-}" in
  notes) [[ $# -eq 4 ]] || { echo "usage: $0 notes <tag> <owner/repo> <out-dir>" >&2; exit 2; }; notes "$2" "$3" "$4" ;;
  body) [[ $# -eq 3 ]] || { echo "usage: $0 body <version> <notes-dir>" >&2; exit 2; }; body "$2" "$3" ;;
  *) echo "usage: $0 notes <tag> <owner/repo> <out-dir> | body <version> <notes-dir>" >&2; exit 2 ;;
esac
