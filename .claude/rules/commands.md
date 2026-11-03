---
paths:
  - "src/Yaat.Sim/Commands/**"
  - "COMMANDS.md"
  - "docs/command-cheatsheet.json"
  - "docs/command-aliases/**"
---

# Command rules

- Match ATCTrainer/VICE names where applicable. Canonical reference is `COMMANDS.md`; alias data lives in `docs/command-aliases/*.json`.
- **Completeness (MANDATORY):** Every `CanonicalCommandType` must exist in `CommandScheme.Default()` and `CommandRegistry.All`. Tests enforce this.
- **Altitude arguments:** Always use `AltitudeResolver.Resolve()` for parsing altitude arguments in commands. It handles both shorthand (`15` → 1500ft) and full (`1500` → 1500ft) formats, plus AGL notation (`KOAK+010`).
- Update `COMMANDS.md` whenever a command is added, removed, aliased, or changes behavior/arguments. This is the canonical user-facing command reference — both the **Quick Reference** tables and the **Detailed Command Documentation** section must stay in sync with the code (`CommandRegistry.All`, `CanonicalCommandType`, handler behavior).
- Update the in-app command cheatsheet when a command's quick-reference changes: edit the row data in `docs/command-cheatsheet.json`, then run `node tools/build-cheatsheet.mjs` to regenerate `docs/command-cheatsheet.html`. Never hand-edit the HTML — `prek` + CI run `--check` and fail the build if JSON and HTML drift.
