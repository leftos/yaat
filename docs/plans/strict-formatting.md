# Strict C# formatting for yaat + yaat-server, canonical config in the language-conventions skill

## Context

delve-the-dungeon fails its build and CI on style drift: `EnforceCodeStyleInBuild` + warnings-as-errors, a rule-bearing
`.editorconfig`, `dotnet csharpier check .` and `dotnet format --verify-no-changes --severity info` in CI. yaat has none of that:
its `.editorconfig` enforces only braces, `Directory.Build.props` carries only the version, CI never checks formatting, and the
prek `dotnet format` hooks run at default severity on staged files only. Goal: yaat and yaat-server become as strict as delve,
every existing violation is auto-fixed or hand-fixed, and the convention lives in one canonical place for all C# repos.

Decisions taken (AskUserQuestion, 2026-09-18): **var only when the type is apparent** (delve's policy) is the global rule;
**style only** in this pass, CA code-quality rules become a plan item; **canonical files in the skill, convert yaat + yaat-server
only**.

Measured today (solution build, every Style rule at warning, delve's option values; log `.tmp/style-measure.log`,
unique list `.tmp/style-unique.txt`):

- CSharpier: 0 drift over 2,176 files. Line endings: index is all LF; 341 working-tree files are CRLF (local checkout only).
- delve's warning-level set: IDE0008 35,823 · IDE0022 1,168 · IDE0305 360 · IDE0290 75 · IDE0130 42 · IDE0300 31 · IDE0301 16 ·
  IDE0007 8 · IDE0065 5 · IDE0011/IDE0161/IDE0040/naming 0. ~3,845 of all hits are in yaat-server.
- Info-level rules the CI verify will also flag (default `suggestion`): IDE0370 717 · IDE0017 574 · IDE0042 268 · IDE0031 137 ·
  IDE0032 116 · IDE0078 96 · IDE0028 96 · IDE0060 57 · IDE0059 46 · IDE0039 44 · IDE0051 32 · IDE0330 27 · IDE0200 20 …
  (supersedes the ~1,930 estimate in `docs/plans/MAIN.md` Wave 10).
- Rules silent by default and outside delve's set (IDE0058 4,259, IDE0046 896, IDE0048 332, IDE0010/IDE0072 …) stay off.
- CA rules under `AnalysisLevel=latest-recommended`: ~12k (CA1707 8,938, CA1848 1,471, CA1873 805, CA1305 319, rest ~600) —
  deferred by decision, recorded as a plan item.

Changed during execution (2026-09-18): `TreatWarningsAsErrors` + `EnforceCodeStyleInBuild` landed with the config in step 2
rather than step 6, so each rule family's build gate proves the rule is clear; CSharpier 1.3.0 formats `.axaml`, and by
decision it owns XAML formatting too (55 files, one mechanical commit); the CI verify runs the `style` and `analyzers`
subcommands separately, never a bare `dotnet format`.

Out of scope: non-C# formatters beyond what exists (ruff stays; no new JS/PowerShell/XAML formatter), other C# repos,
`AnalysisLevel`, CA1502.

## Steps

Each step is its own commit (ask before each), gate green first. Mechanical rewrites are split from config per
`feedback_split_refactor_from_feature`. Start only when both checkouts are clean and no other session is writing —
`main` moved during planning (1a9eb1a2 → e2a23631).

### 0. Plan bookkeeping (orchestrator)
- `docs/plans/MAIN.md` Wave 10: replace the line-131 item with a checkbox linking a new subplan
  `docs/plans/strict-formatting.md` (this plan, with the counts); add a second checkbox: "Adopt
  `AnalysisLevel=latest-recommended` — CA1707 off under `tests/`, CA1848/CA1873/CA1305 rule by rule" with the counts above.

### 1. Canonical config in the skill (orchestrator; load `writing-skills` first)
- New `~/.claude/skills/language-conventions/csharp/`: `.editorconfig` (delve's file verbatim minus the CA1502 block),
  `Directory.Build.props.fragment` (`TreatWarningsAsErrors`, `EnforceCodeStyleInBuild`; `AnalysisLevel` listed as the
  full-strictness line), `prek-hooks.toml` (csharpier + format-style + build hooks), `ci-steps.yml` (the two check steps).
- `SKILL.md` C# section: point to those files as the source to copy, state the var policy explicitly, and note that a repo
  adopting them fixes existing violations in rule-family commits. Commit in the `~/.claude` repo.

### 2. Config lands, rules at `suggestion` where violations exist (orchestrator, inline — config only)
Both repos: copy the canonical `.editorconfig` over the existing one, **keeping yaat's ReSharper/Qodana and `[*.axaml]`
blocks**; add `.csharpierignore` (`.claude/worktrees/`, `.tmp/`); bump CSharpier 1.2.6 → current stable (look it up; delve pins
1.3.0) in both `dotnet-tools.json`, reformat if the bump changes output. To keep `main` buildable between steps, the
violating rules start at `suggestion` and each later step raises its rule to `warning` in the same commit that fixes it.
Gate: `tools/gate.sh .tmp/build.log dotnet build -p:TreatWarningsAsErrors=true`; `dotnet csharpier check .`.
Also refresh the 341 CRLF working files (`git add --renormalize .` shows nothing staged → re-checkout those paths); no commit.

### 3. IDE0008/IDE0007 var policy — the 36k-site rewrite (implementer, alone)
`dotnet format style yaat.slnx --diagnostics IDE0007 IDE0008 --severity info` (yaat.slnx includes the sibling server project;
run the same against `yaat-server.slnx` for its tests/tools), then `dotnet csharpier format .`, raise both rules to `warning`,
build with warnings-as-errors. One commit per repo; add each SHA to a new `.git-blame-ignore-revs` (+ a follow-up commit, and
`git config blame.ignoreRevsFile` note in CONTRIBUTING). Gate: build + `pwsh tools/test-all.ps1` (semantically neutral, so
replay determinism must hold untouched).

### 4. Remaining warning-level families (implementer, one brief)
In order, each its own commit and build gate: IDE0022 (expression bodies) → IDE0300/IDE0301/IDE0305/IDE0028 (collection
expressions) → IDE0290 (primary constructors, 75 — read the diff: captured parameters lose `readonly`; keep a field where the
class mutates or the MVVM generator needs one) → IDE0065 → IDE0130 (42, by hand: before renaming, `rg` the type name in
`Simulation/Snapshots/`, `RecordingArchive`, MessagePack/JSON discriminators and yaat-server; a persisted type name gets a
justified `#pragma`/`[SuppressMessage]` instead of a rename). Gate after the last: `pwsh tools/test-all.ps1`.

### 5. Info-level sweep (implementer, fresh brief)
`dotnet format style yaat.slnx --severity info`, then `dotnet format analyzers … --severity info`, csharpier. Hand-fix what has
no fixer (IDE0060 unused parameters, IDE0051 unused members — delete, or justify a suppression for reflection/XAML/generator
use). Behaviour-risk fixers get a diff read before commit, by family: IDE0017 (initializer evaluation order), IDE0031,
IDE0032, IDE0059 (never drop a call with side effects), IDE0370. `csharp-reviewer` on those diffs only; no
`aviation-sim-expert` (no behaviour change intended — any test delta is a bug in the rewrite, revert that hunk).
Done when `dotnet format yaat.slnx --verify-no-changes --severity info` and the yaat-server equivalent exit 0.
Gate: `pwsh tools/test-all.ps1`.

### 6. Make it permanent (orchestrator + one implementer brief for the hook scripts)
- `Directory.Build.props` in both repos: `TreatWarningsAsErrors`, `EnforceCodeStyleInBuild` (yaat-server keeps its
  `YaatSimProject` block). Verify the Avalonia/WASM and generated code build clean under it.
- prek, both repos: `tools/hooks/dotnet-format-wrapper.sh` passes `--severity info`; yaat-server's builtin
  whitespace hooks stay; yaat keeps `whitespace-fix-autostage`.
- CI, both `ci.yml`: add `dotnet tool restore` + `dotnet csharpier check .` + `dotnet format <sln> --verify-no-changes
  --severity info --no-restore` after Restore; run `actionlint` and `zizmor` on the edited workflows.
- Docs: `CLAUDE.md` Build & Format section (both repos) and `CONTRIBUTING.md` state the rules and the verify command;
  `docs/architecture.md` if it lists the root config files; delete `docs/plans/strict-formatting.md` and tick/remove the MAIN.md
  item. No CHANGELOG entry (internal, no user-facing change).

## Verification

1. `dotnet csharpier check .` → exit 0 in both repos.
2. `dotnet format yaat.slnx --verify-no-changes --severity info` and `dotnet format yaat-server.slnx …` → exit 0.
3. `tools/gate.sh .tmp/build.log dotnet build -p:TreatWarningsAsErrors=true` → 0 warnings.
4. `pwsh tools/test-all.ps1` green after steps 3, 4, 5 and at the end.
5. Negative proof: in a scratch edit, add `var x = Foo();` and a braceless `if` → build fails with IDE0008/IDE0011; add a
   mis-indented line → `csharpier check` fails; revert.
6. `prek run` on a staged `.cs` file auto-fixes and re-stages an info-level violation.
7. `git ls-files --eol | rg -c "w/crlf"` → 0.
