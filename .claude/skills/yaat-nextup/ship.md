# yaat profile: Ship

Read by the user-level `ship`; each rule names the phase it adds to or overrides.

- Sibling (Phase 0): yaat-server, shipped by the same phases. Source: the `yaat-server` beside the yaat worktree when it is on the same branch (a paired session), else the main checkout's sibling. Target: always the main checkout's sibling, `"$(cd "$(git rev-parse --path-format=absolute --git-common-dir)/.." && pwd)/../yaat-server"`; never a drive letter.

  Its first `status -sb` line halts on `## HEAD (no branch)` like yaat's (yaat-server sits detached at `origin/main` after a submodule-style update). A dirty yaat-server source holding the session's edits goes through Phase 1 first; a yaat-server with no local-only commits against `main` is `Phase 2: yaat-server nothing to land`.
- Orders (Phase 2, Phase 2F, Phase 4):

  | | First | Then | Why |
  |---|---|---|---|
  | Landing | yaat | yaat-server | Default. A `Yaat.Sim` signature change yaat-server calls lands yaat-server first (the deadlock rule below): a fast-forward runs no hooks. |
  | Push | yaat | yaat-server | yaat-server's `ci.yml` builds against yaat's `main` (a PR: yaat's same-named branch, else `main`), not the `extern/yaat` pin, so yaat's commits must be on `origin/main` before the server push that uses them. yaat's own CI builds and tests yaat-server's `main` (a PR: the same-named branch) against each yaat commit, so a server break shows on the yaat commit that caused it. |

- Feature PR (Phase 0, Phase 2F): one per repo that carries the branch, `gh pr view feat/<name> --repo leftos/yaat --json number,state,baseRefName` and the same with `--repo leftos/yaat-server`. Phase 2F runs per repo in the landing order; the project's tracking issue is **land**ed once, after both PRs merge.
- Additive conflict files (Phase 2, Phase 2F): `CHANGELOG.md`, `docs/plans/*.md` other than the snapshot, `docs/architecture.md`. A conflict on the snapshot `docs/plans/MAIN.md` takes either side and runs **snapshot** again.
- Hook deadlock (Phase 2): a `Yaat.Sim` signature change yaat-server calls deadlocks the two prek build hooks. yaat's compiles `yaat.slnx`, which builds the sibling yaat-server from disk (old call site, `CS7036`); yaat-server's builds against the sibling yaat (no new API). Land yaat-server first:
  - yaat-server fast-forwards: `git merge --ff-only` makes no commit and runs no hooks; then run or resume the yaat pick, whose hook now sees the new call site.
  - yaat-server needs a real cherry-pick: start the yaat cherry-pick first and let it pause (a conflict or a hook failure; yaat's tree then holds the new `Yaat.Sim` on disk), cherry-pick yaat-server (its hook builds against that tree), then `git cherry-pick --continue --no-edit` in yaat.
  - Standing hooks-bypass exception (`CLAUDE.md`, "Git & Issues"): an intermediate cherry-pick that cannot pass its hooks until the sibling lands, never pushed on its own, is finished with `git -c core.hooksPath=<empty dir> cherry-pick --continue --no-edit`. The end gate must then pass before anything is pushed.
- Gate (Phase 3), in yaat's main checkout after a real cherry-pick: `pwsh tools/gate.ps1 -Log .tmp/build.log -TimeoutSeconds 300 -Slot heavy -- dotnet build -p:TreatWarningsAsErrors=true`; a `Yaat.Sim` signature change also `pwsh tools/gate.ps1 -Log .tmp/test-all.log -TimeoutSeconds 360 -Slot heavy -- pwsh tools/test-all.ps1`.
- End gate (Phase 3), after the deadlock path or after any signature-changing commit lands on a diverged `main`, before anything is pushed, in both main checkouts (yaat-server has no gate of its own; call `../yaat/tools/gate.ps1` from it): the gated build in each, then the gated `test-all.ps1` in yaat's, which builds and tests both.

  Report those results, not "hooks passed". A break it finds is fixed forward on `main`; a DTO or wire mapping fix goes into one shared helper both call sites use, never a duplicated switch.
- Large-file hook (Phase 2): a recording bundle (`tests/Yaat.Sim.Tests/TestData/*.zip`) over the `check-added-large-files` limit in `prek.toml` is trimmed, never exempted: cut the manifest's `Snapshots` to the time range the test uses.
- Submodule (Phase 2, Phase 4): yaat-server's `extern/yaat` pin is not kept current and nothing reads it for a build that matters (yaat-server `CLAUDE.md`); never bump it by hand, and leave a stale pin alone.
- Issues (Phase 5): the issue repository is always `leftos/yaat`, also for a yaat-server-only fix. yaat has a release pipeline, so Phase 5 comments and never closes: `#N landed, open until release`. Comment lines name `leftos/yaat@<sha>` and `leftos/yaat-server@<sha>`.
- Branch names (Phase 5): `nightly-review/<date>-<slug>` encodes a date, not an issue.
