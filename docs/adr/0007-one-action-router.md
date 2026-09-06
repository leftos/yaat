---
status: accepted
date: 2026-09-06
---

# One action router: every controller action takes one route on every run kind

A controller action — a typed command, a CRC keyboard entry, an AI position's instruction, a recorded
line replayed — used to be routed by three independent parse-and-decide chains: the live
`RoomEngine.SendCommandAsync` (about thirty-seven arms), the Sim replay `SimulationEngine.ReplayCommand`
and the server reconstruction `RecordingManager.ReplayCommand`. The tick oracle (ADR 0004) compares
world state, so it could not see the chains disagree: a verb the live chain applied and a replay chain
dropped simply never entered the state it compared. The 2026-09-05 audit found that disagreement was a
class, not a case — `SQALL` / `SNALL` / `SSALL`, `TAXIALL`, `TDLSOPS`, `ASDXALERTS` and `ADD` silently
no-op'd on every replay and bundle export (global commands recorded with an empty callsign, dropped by
an aircraft-exists guard); reconstruction ignored the pilot-reaction delay and never rebuilt the solo
evaluator; two track tables and two identity resolvers had opposite AI-branch precedence; and a whole
census of CRC handlers wrote sim or room state with no record at all.

**Decision.** `Yaat.Sim` owns one `ActionRouter`, and every entry point — the live room, the CRC
handlers, the AI controller sink, the Sim replay driver, the server reconstruction, tape playback and
the bare test engine — issues or re-applies through it. The route is fixed: strip the `AS` prefix →
refuse a chain with a non-compoundable verb → split a scoped-special compound into units → classify
(`RecordedCommandClassifier`, exhaustive: an unclassifiable verb throws rather than falling into a
default) → resolve the action's scope (`Global` / `Callsign` / `Aircraft` / `Position`, a property of
the kind, not of the recorded callsign) and its identity (`TrackResolver.ResolveIdentity`, one
resolver, one `PositionSelections` map) → run the arm table's row → record. The run kind is not an
input to any of it: the router asks its `IActionHost` for a body or an answer, never whether this is
a replay (ADR 0005).

The consequences that were genuine choices:

- **An arm is a Sim body or a host slot, and the table has no refusal row.** A kind whose state lives
  in `Yaat.Sim` runs there on every run kind; a kind whose state the server still owns (strips, TDLS,
  coordination, ASDE-X alert inhibits, bookmarks, the clock) runs through a slot the host fills, and
  the bare and replay hosts answer honestly (refuse or no-op) rather than pretending. Each slot is
  listed as step-4 debt; the table is complete now, not when step 4 lands.
- **Every routed command is recorded with its verdict, accepted or not** (`RecordedCommand.Accepted`).
  A replay that reaches the other verdict logs a `replay-fidelity` warning; it is never silenced by
  skipping the record. Before this the live chain recorded successes only, so a refused live command
  was invisible to a bundle.
- **The full post-dispatch mirror runs on every run kind.** Read-backs, "unable", the frequency gates,
  contact registration and evaluator scoring are simulation state; whether anything is broadcast is
  the host's decision (`RoomHost.Replaying`). Reconstruction used to run a contact-only subset.
- **What a live run draws is baked onto its record and reused, never redrawn.** The pilot-reaction
  delay, the `REL` spawn jitter, the aircraft an `ADD` generated, the `CFR` clock, the id a manual
  strip request minted. An `ADD` still *derives* the spawn against the shared RNG and beacon pool on
  replay — so both streams advance exactly as live — and the baked snapshot is the authority when the
  derivation disagrees.
- **A CRC entry that has a typed verb is issued as that verb's text** under `AS {tcp}` (the STARS
  coordination entries, the console `C{recv}{send}[+]` consolidation forms, the position sync as the
  connection's own `AS {code}`), so one arm serves the keyboard and the terminal and the canonical
  text must round-trip the parser (`CommandDescriber` ∘ `CommandParser` is the identity per field —
  two describe∘parse mismatches were found and pinned on the way).
- **A CRC write no verb covers is a derived record**, produced by the handler that made it and
  applied through the same body a replay uses: STARS shared state, clearances, hold annotations, the
  ERAM keyboard entries (`EramEntryEngine`, one grammar), CRR groups, strip requests, the ASDE-X
  safety-logic configuration. `IssueDerived` appends the record only when the write applied — a live
  refusal is the verdict, not a fidelity break — while `ApplyRecorded` treats a refusal as one.
- **Deliberately not recorded**, each for a stated reason: transport verbs (`PAUSE` / `UNPAUSE` /
  `SIMRATE` — the room's clock, not simulation state; legacy records stay inert), bookmarks
  (timeline-global metadata the rewind paths carry over verbatim; replaying an add would duplicate
  them), the `SHOW` query (read-only), aircraft assignments (room policy applied *before* the router —
  a command it refuses never reached the simulation), `TdlsDumped` and the rest of TDLS state (in no
  snapshot until step 4; a replayed `TDLSQ` would duplicate live state, so the slots refuse while
  replaying), and surface temp data (drawn areas, labels, presets — facility furniture that
  `FacilityTempDataStore` persists across rooms and restarts, so a rewind must not un-draw what the
  store still holds). Attendance becomes a recorded input in step 4 (ADR 0003).

**Rejected.** Keeping three chains and adding parity tests: the oracle's blind spot is structural,
and a test per verb is exactly the list-in-two-places shape ADR 0001 forbids. A replay-only router with
the live chain left alone: the arms drift again the day someone adds a verb live, which is how the
audit's class came to exist.

**Consequences.** Adding a verb is a `RecordedCommandKind`, an `ArmTable` row and, only if its state
is still the room's, an `IActionHost` slot — nothing is added to any entry point. The `actions`
oracle fixture drives a script of global, position-scoped, reaction-delayed and refused commands
through every leg, and its three baselines emptied as the sub-commits landed (69 → 0 on reconstruct);
the twelve baselines were byte-identical through the Class B work, which is the check that recording
a handler's write changes nothing live. A rewind now restores position selections, ownership,
consolidations, CRC display state and the CRC-entered clearances as of its target. Two chains'
worth of code left: `TrackCommandHandler`, `ReplayTrackApplier`, `RecordingManager.ReplayCommand`
and the thirty-arm `SendCommandAsync` are gone. The evidence per sub-commit — predicted-vs-got on the
baselines, the corpus triage, the corrections to the plan — is in
`docs/plans/tick-path/03d-action-router.md` until that plan is deleted, then in git history.
