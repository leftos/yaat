# Security Policy

## Reporting a vulnerability

**Please do not report security vulnerabilities through public GitHub issues, discussions, or pull requests.**

Email **leftos@gmail.com** with:

- A description of the vulnerability.
- The version, commit, or branch you found it on.
- Steps to reproduce, ideally with a proof-of-concept.
- Any thoughts on impact and how it could be exploited.
- Whether you'd like to be credited in the fix, and how.

If you want to encrypt the report, ask first and a PGP key will be provided.

## What to expect

| Stage | Target turnaround |
|-------|-------------------|
| Acknowledgement that we received your report | 7 days |
| Initial assessment (is this in scope, severity, who's working on it) | 14 days |
| Fix shipped or coordinated disclosure agreed | 90 days from acknowledgement |

These are targets, not guarantees — YAAT is a volunteer project. We'll keep you in the loop if something is going to slip.

We follow coordinated disclosure: once a fix is shipped (or 90 days have elapsed, whichever comes first), the issue and the reporter's credit can be made public. If you need to disclose earlier — for example, the vulnerability is already being exploited — say so in your report and we'll work with you on the timeline.

## Scope

**In scope:**

- The YAAT desktop client ([leftos/yaat](https://github.com/leftos/yaat))
- The YAAT server ([leftos/yaat-server](https://github.com/leftos/yaat-server), private — server-side issues still get reported here)
- The shared simulation library (`Yaat.Sim`)
- Release artifacts (installers, AppImages, portable archives) published on the [Releases page](https://github.com/leftos/yaat/releases)

**Out of scope — report to the upstream project:**

- Vulnerabilities in vNAS (CRC, the data API, the auth service): report at <https://vnas.vatsim.net/>
- Vulnerabilities in third-party dependencies that don't directly affect YAAT: report to the upstream maintainer. If they intersect with YAAT (e.g., a sandbox escape we expose through user input), please report to us too.

## What counts as a vulnerability

Examples we'd like to hear about:

- Remote code execution on the client or server.
- Auth/authz bypasses (e.g., a non-admin client gaining admin powers, or a client gaining access to a training room it doesn't belong in).
- Credential or token leakage through logs, the wire protocol, or shipped artifacts.
- Crashes or denial of service that an unauthenticated attacker can trigger.
- Supply-chain issues in the release pipeline (compromised build artifacts, missing signatures, etc.).

Bugs that don't have a security impact should be filed as regular issues, not security reports.

## Public credit

If you'd like to be credited, your name and a link (GitHub, blog, etc.) will go in the changelog entry and the release notes for the fix. Anonymous reports are equally welcome.

## Out-of-band contact

If for some reason `leftos@gmail.com` is unreachable, post in the YAAT Discord (linked from the repo description) asking a maintainer to DM you — do not paste the vulnerability details into a public channel.
