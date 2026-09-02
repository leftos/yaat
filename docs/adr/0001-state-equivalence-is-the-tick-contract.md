---
status: accepted
date: 2026-09-02
---

# State-equivalence is the contract between runs

Four named paths and around twenty entry points advance a sim-second, resolving to two independent
post-physics lists plus an undocumented end-of-second tail. `docs/tick-loop.md` prescribes a seam
convention for keeping them aligned, but nothing enforces it, and a step added to one list and not
the other ran dark on the live server for about two and a half months without a test noticing.

We commit to **state-equivalence**: given the same inputs, every run kind produces the same world
state. Not merely "the same steps exist on both paths" — that weaker contract is what an ordered list
of step names can guarantee, and it is blind to the failure we actually keep hitting, which is a step
present on both paths and *implemented twice with different logic*. Weather advance exists in five
places with three different semantics; the physics sub-tick rate is declared as two independent
`const int`s in two repos; `DrainAllApproachScores` is discarded on one path and consumed on the
other. A step-name comparison sees none of that.

The scope is therefore the **whole sim-second**, including the end-of-second work, because weather
and position history are snapshot-captured state mutated there.

The direction of travel is that `Yaat.Sim` owns simulation and the server owns comms — see
[0003](0003-simulation-logic-moves-into-yaat-sim.md). That is a direction each change moves toward,
not a milestone with a finish line.

## Consequences

- The spine is one ordered definition in `Yaat.Sim` that every run iterates; divergence becomes
  unrepresentable rather than merely tested against.
- Verification is mechanical rather than a matter of classifying steps correctly — see
  [0004](0004-the-oracle-and-the-corpus.md).
- This work takes priority over the controller-AI milestone. It was previously scoped as Phase 1 of
  that milestone under requirement DET-03, and being scoped that way is what confined it to
  post-physics, where the brains run.
