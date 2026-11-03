---
paths:
  - "tools/**/*.py"
  - "docs/**/*.py"
  - "pyproject.toml"
---

# Python tooling

`pyproject.toml` at the repo root configures ruff (150-char lines, py313, complexity ≤8) for the scripts in `tools/` and `docs/`. Lint with `uvx ruff check tools`, format with `uvx ruff format tools`. Pre-config complexity debt is grandfathered via per-file ignores in `pyproject.toml` — new scripts must meet the limits.
