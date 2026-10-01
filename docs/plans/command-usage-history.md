# Command usage history

The desktop client keeps a local, per-user record of the commands the user entered and the server accepted, keyed by scenario and by primary airport. A new window lets the user sort and filter that record and add any entry to their favorites in one action. Index line: [MAIN.md](./MAIN.md), under **Bug reports and feature requests**.

## Decisions (user 2026-10-01)

- **Callsign-free, local only.** An entry never records which aircraft it was sent to. The record is a per-user file on this machine and is never sent anywhere.
- **Granularity.** An entry is the command's exact text with its alias resolved to the canonical verb: `C 50` and `CM 50` are one entry, `CM 50` and `CM 60` are two.
- **Sources.** Commands typed into the command box and commands the speech pipeline produced count. Context-menu clicks, favorites and strip actions do not.
- **Compounds.** A compound line records each of its commands, plus the whole line as a separate compound entry, so a chain can be favorited as one.
- **Window.** One table with a scope selector (this scenario / this airport / all; default this scenario), sortable columns (count, last used, command) and a text filter, with an "add to favorites" action per row.

## Open

Exploration under way: the send path and how the client learns acceptance, callsign stripping and alias canonicalization (and commands whose arguments name another aircraft), scenario and primary-airport identity, today's favorites shape, persistence and window conventions.
