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
7. Version numbers live in more than one file and do not all move together. Work Step 3a's table and state a verdict per file — in particular, yaat-server's `Yaat:ClientVersions:Recommended` bumps every release while `Minimum` bumps only when the cycle breaks older clients.

## Output

Show the draft highlights, full changelog section, heading promotion, and the Step 3a version-file verdicts before asking for approval, matching the canonical command.

Also determine and report the deployment-scope verdict (client-only vs server-affecting) as the canonical command's Step 7 describes: if every change since the previous release is confined to the desktop client — nothing in `Yaat.Sim`, the server, or the web-deployed UI (vStrips/vTDLS) — recommend skipping the droplet deploy to avoid downtime, and default the push prompt accordingly.

On a server-affecting release, the deploy is two-stage: push, front-load the ~20-minute CI image build (`deploy-to-droplet.ps1 -BuildImageOnly`, no downtime), and only when the image is ready check room occupancy and ask whether to deploy (`-SkipCiBuild -NoLogs`, ~2 minutes of downtime). Never ask the deploy question before the image build has finished — occupancy changes over the build window.
