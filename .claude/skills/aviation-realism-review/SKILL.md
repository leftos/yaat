---
name: aviation-realism-review
description: "Review YAAT aviation logic, phraseology, pilot behavior, ATC rules, aircraft performance, and simulation changes against local FAA 7110.65 and AIM references."
---

# Aviation Realism Review

This is the Codex wrapper for the canonical Claude agent at `.claude/agents/aviation-sim-expert.md`.

Use this skill for any YAAT task touching flight physics, pilot AI, ATC logic, radio communications, aircraft performance, phase transitions, command dispatch, ground operations, conflict detection, trigger conditions, or automatic aircraft behavior.

## Workflow

1. Read `.claude/agents/aviation-sim-expert.md` before reviewing or designing the change.
2. Read the relevant local FAA index first:
   - `.claude/reference/faa/7110.65/INDEX.md`
   - `.claude/reference/faa/aim/INDEX.md`
3. Read specific local FAA markdown sections and cite chapter, section, and paragraph numbers when making procedural claims.
4. Do not use web search for FAA 7110.65 or AIM content that is already in the repo.
5. Review the changed files or plan for realism, units, sequencing, phraseology, and training value.
6. Report concrete findings with file and line references when reviewing code.

## Output

Lead with issues or required changes. If there are no aviation-realism concerns, say that clearly and list any remaining assumptions or missing source checks.
