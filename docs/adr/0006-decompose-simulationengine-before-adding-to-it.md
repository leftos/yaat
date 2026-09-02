---
status: accepted
date: 2026-09-02
---

# Decompose SimulationEngine before adding to it, and build the oracle first

`SimulationEngine.cs` is 5,543 lines and 175 members in a single non-partial class, the largest and
most-churned file in the repo. This work would add a spine, an end-of-second phase and the relocated
server logic to it. Decomposition is therefore a prerequisite step, not a follow-up, and it settles a
backlog item that has been open undecided.

The mechanism is a partial-class split by cluster, following the `MainViewModel` precedent already
documented in `docs/client-mainviewmodel.md` — no public-surface change, no call-site churn — plus
one genuine extraction: the replay drivers, 426 lines across 19 members with zero yaat-server
production call sites. That cluster is the one that is actually separable, and extracting it is what
replay-as-a-run-kind wants anyway. Everything else stays partial until it earns extraction: `Scenario`
and `World` are touched by ten of the twelve clusters, so a broader extraction means threading both
through nearly everything, against several hundred test call sites per cluster.

**The oracle lands before the decomposition.** Decomposition is a large behaviour-preserving refactor
and the oracle is precisely the instrument that proves it preserved behaviour; built first against
today's divergent code, with the current divergences recorded as its accepted starting state, every
later step inherits the same proof. Each of those accepted entries is then retired deliberately.

The whole programme lands incrementally to main, each step green on its own, rather than on a
long-lived branch — which is also the only shape under which per-recording desync triage is tractable,
since desyncs then arrive in small batches attributable to one change.
