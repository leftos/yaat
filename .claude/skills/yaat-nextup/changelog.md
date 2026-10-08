# yaat profile: Changelog

Read by the user-level `changelog-and-commit`; each rule names the step it adds to or overrides.

- Home: one `CHANGELOG.md`, at the yaat checkout root. yaat-server has none; its user-visible changes are bulleted in yaat's file. Step 2b: **land** each item after its commit.
- Sibling (Step 0, Step 1): yaat-server, snapshotted and scoped like yaat. It is the `yaat-server` beside the current yaat checkout when that is a checkout on the same branch (a paired worktree), else the main checkout's sibling, `"$(git rev-parse --path-format=absolute --git-common-dir)/../../yaat-server"`; never a drive letter. Naming the main checkout's sibling from a paired session reports the server side clean and commits nothing there.
- Commit order (Step 8): yaat-server first, its code and tests, prefix from the work; then yaat, `CHANGELOG.md` plus any yaat scope, `docs:` when the changelog is all it carries, subject and body mirroring the bullets. A yaat-server branch behind `origin/main` runs `git pull --ff-only` before its commit. A diff wholly in yaat is one commit.
- Hooks (Step 8): per repo. A hook that reformats a file means re-stage and a new commit in that repo; the other repo's commit is unaffected.
- Announcement and report (Step 5, Step 8): name both repos and which files go in which commit; the report prints both HEADs, `Committed yaat-server@<sha> — <subject>`, `Committed yaat@<sha> — <subject>`, `Working trees: clean / clean`.
- Reference (Step 2): `### Fixed` against `### Added` is judged against the emulated upstream (vNAS TDLS, CRC/STARS, ATCTrainer). A YAAT-original convenience the upstream lacks (a Tools-menu shortcut, an instructor-only view) is Added.
- Reach vocabulary (Step 2): all rooms, solo sessions only, headless/soak only, replay only.
- Audience (Step 4): instructors and students. No framework names (Velopack, Avalonia, SignalR, MessagePack); command names and UI vocabulary stay (`CTOC`, the "Update Now" button, Help → About).
- Issue references (Step 7): a commit in either repo that resolves a yaat issue writes `Refs https://github.com/leftos/yaat/issues/N`, never a closing keyword (`## Plan and tracker`).
- Partial commits (Step 8): the build hook is cross-repo (`CLAUDE.md`, "prek's build hook is cross-repo"), so a partial commit in yaat stashes yaat-server too: `git -C <yaat-server> stash push -u`, commit, `git -C <yaat-server> stash pop`.
- Commit call (Step 8): never chain `git commit` after a `dotnet` command in one shell call; a guard reads the whole command text and rejects it. Implicated tests run through the gate as `## Agents and gates` says.
