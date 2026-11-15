# yaat profile: Ship

Read by the user-level `ship`; each rule names the phase it adds to or overrides.

- Sibling (Phase 0): yaat-server, shipped by the same phases. Source: the `yaat-server` beside the yaat worktree when it is on the same branch (a paired session), else the main checkout's sibling. Target: always the main checkout's sibling, `"$(cd "$(git rev-parse --path-format=absolute --git-common-dir)/.." && pwd)/../yaat-server"`; never a drive letter.

  Its first `status -sb` line halts on `## HEAD (no branch)` like yaat's (yaat-server sits detached at `origin/main` after a submodule-style update). A dirty yaat-server source holding the session's edits goes through Phase 1 first; a yaat-server with no local-only commits against `main` is `Phase 2: yaat-server nothing to land`.
- Orders (Phase 2, Phase 2F, Phase 4):

  | | First | Then | Why |
  |---|---|---|---|
  | Landing | yaat | yaat-server | Always, a `Yaat.Sim` signature change yaat-server calls included: yaat's hooks never build yaat-server, and yaat-server's hooks build against the sibling yaat, which then already holds the new `Yaat.Sim`. |
  | Push | yaat | yaat-server | yaat-server's `ci.yml` builds against yaat's `main` (a PR: yaat's same-named branch, else `main`), not the `extern/yaat` pin, so yaat's commits must be on `origin/main` before the server push that uses them. yaat's own CI builds and tests yaat-server's `main` (a PR: the same-named branch) against each yaat commit, so a server break shows on the yaat commit that caused it. |

- Feature PR (Phase 0, Phase 2F): one per repo that carries the branch, `gh pr view feat/<name> --repo leftos/yaat --json number,state,baseRefName` and the same with `--repo leftos/yaat-server`. Phase 2F runs per repo in the landing order; the project's tracking issue is **land**ed once, after both PRs merge.
- Additive conflict files (Phase 2, Phase 2F): `CHANGELOG.md`, `docs/plans/*.md` other than the snapshot, `docs/architecture.md`, a subsystem doc's `## File map` section (two entries added at one spot), `docs/CONTEXT.md` (two glossary sections or entries added at one spot), `docs/releases/whats-new-next.md` (two feature sections added at one spot). A conflict on the snapshot `docs/plans/MAIN.md` takes either side and runs **snapshot** again.
- yaat's build hook is yaat-only (Phase 2, Phase 4): `prek.toml`'s pre-push `dotnet build` compiles `yaat.slnx`, which names no `yaat-server` project and no project referencing one (GuideCapture is a yaat-server tool). A `Yaat.Sim` signature change yaat-server calls therefore passes yaat's hook and is not caught there: it is caught by `tools/test-all.ps1` (the end gate below) and by the yaat-server step of yaat CI, so run `test-all.ps1` before the push.

  yaat-server's own hooks still build against the sibling yaat working tree as it stands, uncommitted edits included, so a yaat-server push from a tree whose on-disk code depends on a `Yaat.Sim` change missing from the sibling yaat fails with `CS0246`/signature errors. Land yaat first, and push from clean trees in both repos.
- Gate (Phase 3), in yaat's main checkout after a real cherry-pick: `pwsh tools/gate.ps1 -Log .tmp/build.log -TimeoutSeconds 300 -Slot heavy -- dotnet build -p:TreatWarningsAsErrors=true`; a `Yaat.Sim` signature change also bare `pwsh tools/test-all.ps1`, which gates itself.
- End gate (Phase 3), after any signature-changing commit lands on a diverged `main`, before anything is pushed, in both main checkouts (yaat-server has no gate of its own; call `../yaat/tools/gate.ps1` from it): the gated build in each, then the bare `test-all.ps1` (self-gated) in yaat's, which builds and tests both.

  Report those results, not "hooks passed". A break it finds is fixed forward on `main`; a DTO or wire mapping fix goes into one shared helper both call sites use, never a duplicated switch.
- Large-file hook (Phase 2): a recording bundle (`tests/Yaat.Sim.Tests/TestData/*.zip`) over the `check-added-large-files` limit in `prek.toml` is trimmed, never exempted: cut the manifest's `Snapshots` to the time range the test uses.
- Submodule (Phase 2, Phase 4): yaat-server's `extern/yaat` pin is not kept current and nothing reads it for a build that matters (yaat-server `CLAUDE.md`); never bump it by hand, and leave a stale pin alone.
- Issues (Phase 5): the issue repository is always `leftos/yaat`, also for a yaat-server-only fix. yaat has a release pipeline, so Phase 5 posts nothing and closes nothing: `#N closed by land` (Phase 4's **land** commented that the fix ships in the next release, and its move to Done closed the issue through the sync). An issue broader than what shipped still gets a comment naming `leftos/yaat@<sha>` and `leftos/yaat-server@<sha>`.
- Branch names (Phase 5): `nightly-review/<date>-<slug>` encodes a date, not an issue.
