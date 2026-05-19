# Governance

How decisions get made in YAAT, how releases happen, and what to expect from the project.

## Project structure

YAAT is two repositories:

- **[yaat](https://github.com/leftos/yaat)** (public, MIT) — the desktop client (`Yaat.Client`), the shared simulation library (`Yaat.Sim`), tests, and tooling.
- **[yaat-server](https://github.com/leftos/yaat-server)** (private) — the ASP.NET Core server. It implements a backend-protocol emulation compatible with vNAS-style infrastructure, and after the planned vNAS integration it will also carry the pipes through which YAAT talks to the real vNAS servers. The repo is kept private to make it non-trivial for bad actors to study and abuse those communication layers — both vNAS itself and YAAT are hobby projects, and that's the threat-model lens. The substantive part of the codebase — the simulation logic — lives in the public `Yaat.Sim` library, and there is no plan to close-source the public repo.

The maintainer set for both repositories is the same (see [MAINTAINERS.md](MAINTAINERS.md)).

## Decision-making

Today the project runs as benevolent-dictator-for-now: Leftos has the final call on direction and merges. The intent is to move to lazy-consensus among maintainers as soon as there's more than one maintainer to form a consensus with.

In practice:

- **Bug fixes and small features** — discuss in the issue/PR, merge when a maintainer is happy with the change.
- **Larger features or breaking changes** — open an issue first (or post in Discussions) to talk about the design before writing code. This avoids burning a contributor's time on an approach that won't be accepted.
- **Disagreements** — discussed in the open on the relevant issue/PR. If maintainers can't agree, Leftos breaks the tie. Once there are multiple maintainers, ties will be broken by maintainer vote with a simple majority.

## Roadmap

Active roadmap lives in `docs/plans/main-plan.md` (public repo). Milestones M0 and M1 are done; M2 (tower operations) is next. The roadmap is a guide, not a contract — what ships depends on contributor availability.

## Releases

- **Versioning** — Pre-1.0 semver: `0.MAJOR.MINOR-alpha`. Breaking changes increment `MAJOR`. The project will declare 1.0 when the feature set is stable enough that breaking changes become rare.
- **Cadence** — Tag-driven, not calendar-driven. Releases ship when there's something worth shipping; expect somewhere between bi-weekly and monthly during active development.
- **Process** — The [`CHANGELOG.md`](CHANGELOG.md) `## Unreleased` section is the source of truth. When a release is cut, the heading is promoted to a version + date, a git tag is pushed, and GitHub Actions builds installers and publishes a release. See the `prepare-release` workflow in `.github/workflows/` for the exact mechanics.
- **Breaking changes** — Called out explicitly in `CHANGELOG.md` under a `### Breaking` subheading. Pre-1.0 the project may make breaking changes without long deprecation windows; the changelog and release notes are the contract.

## How features get in

1. **Open an issue** describing the problem or use case. (Direct PRs without prior discussion are fine for bugfixes and small things; please open an issue first for anything bigger.)
2. **Discuss the approach.** A maintainer comments on the issue confirming direction.
3. **Send a PR.** Reference the issue. Keep the PR focused — one logical change.
4. **Review.** A maintainer reviews. Expect questions; don't take them personally.
5. **Merge.** A maintainer merges once review passes and CI is green.

See [CONTRIBUTING.md](CONTRIBUTING.md) for the development setup, code style, and commit conventions.

## Code of Conduct

All participation — issues, PRs, discussions, Discord — is governed by [CODE_OF_CONDUCT.md](CODE_OF_CONDUCT.md). Maintainers enforce it.

## Security issues

Security vulnerabilities are reported privately, not via public issues. See [SECURITY.md](SECURITY.md).

## Amending this document

Changes to this document are themselves a maintainer-consensus decision. Open a PR with the proposed change and discuss it there.
