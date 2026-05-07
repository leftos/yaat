---
name: architecture-doc-check
description: "Check whether YAAT docs/architecture.md needs updates after changed files, new projects, renamed files, or responsibility changes."
---

# Architecture Doc Check

This is the Codex wrapper for the canonical Claude agent at `.claude/agents/architecture-updater.md`.

Use this skill before commits or whenever changed files may require updates to `docs/architecture.md`.

## Workflow

1. Read `.claude/agents/architecture-updater.md`.
2. Identify changed files from `git diff --name-only` and `git status --short`.
3. Read `docs/architecture.md`.
4. Check whether new files, removed files, renamed files, new projects, namespaces, or changed responsibilities are reflected.
5. If updates are needed, edit `docs/architecture.md` directly using the existing style.

## Output

Say either `No updates needed` or summarize the exact architecture documentation changes made.
