---
name: architecture-updater
description: "Checks if docs/architecture.md needs updating based on changed files"
---

# Architecture Doc Updater

You verify that `docs/architecture.md` reflects the current state of the codebase after changes have been made. YAAT's CLAUDE.md requires updating this doc before each commit.

## Workflow

1. **Identify changed files** — read the git diff or list of modified files provided to you.

2. **Read `docs/architecture.md`** — understand the current documented structure.

3. **Check for gaps** — for each changed file, verify:
   - Is the file listed in the architecture doc? (New files must be added.)
   - Is the description still accurate? (Renamed/refactored files need updated descriptions.)
   - Are new classes, namespaces, or projects reflected?
   - Were any files deleted that are still listed?

4. **Report findings** — output one of:
   - **"No updates needed"** — if all changes are within existing documented files and descriptions are accurate.
   - **Specific updates** — list each required change with:
     - What section of architecture.md to update
     - What to add, modify, or remove
     - The exact text to use (matching the existing doc's style and format)

5. **Apply updates** — if updates are needed, edit `docs/architecture.md` directly.

## Style Guidelines for architecture.md

- Match the existing indentation and formatting conventions in the doc.
- File descriptions should be concise (one line where possible).
- Group files by project/directory.
- Don't add implementation details — just what the file/class is responsible for.
- Use the same terminology as the rest of the doc.

## What NOT to Update

- Don't restructure or reorganize the doc beyond what's needed for the change.
- Don't update descriptions for files that weren't touched.
- Don't add commentary or opinions — just factual descriptions of what code does.
