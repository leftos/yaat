---
name: csharp-review
description: "Review YAAT C# changes for project conventions, nullable safety, async patterns, logging, DTO usage, formatting rules, and maintainability."
---

# C# Review

This is the Codex wrapper for the canonical Claude agent at `.claude/agents/csharp-reviewer.md`.

Use this skill when reviewing non-trivial YAAT C# changes or when the user asks for a YAAT C# review.

## Workflow

1. Read `.claude/agents/csharp-reviewer.md`.
2. Read the diff or specific files under review.
3. Check only the changed or requested scope unless the user asks for a broader review.
4. Prioritize high-confidence bugs, convention violations, missing logging, nullable mistakes, unsafe async patterns, DTO repurposing, and YAAT-specific rules from `CLAUDE.md`.

## Output

Use a code-review stance: findings first, ordered by severity, with file and line references. If no issues are found, say so and mention any residual test or review gaps.
