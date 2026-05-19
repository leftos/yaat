# Maintainers

This file lists the people responsible for YAAT and how the maintainer set evolves.

## Current maintainers

| Name | GitHub | Areas | Contact |
|------|--------|-------|---------|
| Leftos Aslanoglou | [@leftos](https://github.com/leftos) | All of `Yaat.Sim`, `Yaat.Client`, `Yaat.Client.Strips`, build & release, server (`yaat-server`) | leftos@gmail.com |

YAAT is currently a single-maintainer project. That's a bus-factor risk and we know it — the goal of this document is to make the path to additional maintainers explicit so the project can survive any one person disappearing.

## GitHub admin access

Admin access to the [`leftos/yaat`](https://github.com/leftos/yaat) and [`leftos/yaat-server`](https://github.com/leftos/yaat-server) repositories, the release-signing secrets, and the hosted server credentials currently sit with Leftos. Adding a second admin is gated on the project reaching at least two active maintainers (see below) — at that point write/admin access will be shared so the project can continue if one admin is unavailable.

## How to become a maintainer

There's no application form. The path is:

1. **Contribute.** Open issues with clear repro steps, send PRs that fix bugs or add features. Land enough of them that you're a familiar name in the commit log and PR queue.
2. **Help review.** Comment on other people's PRs and issues. Sustained, thoughtful review is most of what a maintainer does.
3. **Stay around.** Maintainer status is offered to contributors who show up consistently over a few months, not after one big PR.

When the existing maintainers agree someone meets that bar, they're invited and given commit access. Maintainers can also propose new maintainers — decision is by consensus among current maintainers.

A maintainer who stops contributing for ~6 months is moved to an "emeritus" section by the remaining maintainers, no drama. They can come back the same way they got in.

## What maintainers do

- Triage incoming issues (label, ask for repro, close duplicates)
- Review PRs on a best-effort cadence (aim for a first response within ~7 days; no SLA — this is volunteer work)
- Cut releases (see [GOVERNANCE.md](GOVERNANCE.md))
- Apply [SECURITY.md](SECURITY.md) for vulnerability reports

## Emeritus maintainers

None yet.
