---
name: prepare-release
description: "Prepare a YAAT release from CHANGELOG.md: verify release secret presence, choose version, draft highlights, promote the changelog heading, commit, tag, and push after approval."
---

# Prepare Release

This is the Codex wrapper for the canonical Claude command at `.claude/commands/prepare-release.md`.

Use this skill when the user asks to prepare, cut, or ship a YAAT release.

## Workflow

1. Read `.claude/commands/prepare-release.md`.
2. Follow its steps interactively and in order.
3. Do not proceed past the review/approval gate before changing version files, committing, tagging, or pushing.
4. Use the unreleased `CHANGELOG.md` section as the source of truth for release notes.
5. Do not scrape Git history as a substitute for missing changelog content.
6. Do not copy or expose secrets. Check only whether required release secrets are configured.

## Output

Show the draft highlights, full changelog section, and heading promotion before asking for approval, matching the canonical command.

Also determine and report the deployment-scope verdict (client-only vs server-affecting) as the canonical command's Step 7 describes: if every change since the previous release is confined to the desktop client — nothing in `Yaat.Sim`, the server, or the web-deployed UI (vStrips/vTDLS) — recommend skipping the droplet deploy to avoid downtime, and default the push/deploy prompt accordingly.
