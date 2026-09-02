# YAAT

Instructor/RPO client and training server for VATSIM air traffic control training. `Yaat.Sim` owns
the simulation; every other project is a way of driving or displaying it.

This glossary fixes the words that have caused real confusion. It is a glossary, not a spec — no
implementation detail, no decisions. Decisions live in [`docs/adr/`](docs/adr/).

## Simulation execution

**Sim-second**:
One second of simulated time, structured as PrePhysics, four physics sub-ticks, PostPhysics, and the
end-of-second steps. The unit every run advances by.
_Avoid_: tick (ambiguous between the sim-second and one physics sub-tick — say which)

**Spine**:
The single ordered definition of the simulation-affecting steps in a sim-second. There is exactly
one, it lives in `Yaat.Sim`, and every run iterates it rather than keeping its own list.
_Avoid_: pipeline, tick list, post-physics list

**Step**:
One member of the spine.
_Avoid_: stage, phase (phase means an aircraft's flight phase in this codebase and nothing else)

**Host**:
The collaborator a run supplies to the spine: it provides each step's arguments and consumes each
step's results. One meaning only, reclaimed from six unrelated prior uses.
_Avoid_: sink, adapter, driver

**Run profile**:
What kind of run this is and, consequently, what is allowed to differ from any other run. Separate
from the host on purpose, so a step can ask its host for an argument without being able to ask it
whether this is a replay.
_Avoid_: mode, context, environment

**Run kind**:
A value of the run profile: live, replay, test, or soak.

**State-equivalence**:
The contract between runs: given the same inputs, every run kind produces the same world state.

**Oracle**:
The mechanical test of state-equivalence — it runs one scenario under several run kinds and compares
the resulting state, rather than relying on anyone having classified the steps correctly.

**Recorded input**:
A value the simulation consumes but does not derive, captured so a replay reproduces it rather than
recomputing it. Attendance is the first of these.

**Attendance**:
Which controller positions are currently being worked. Live, it derives from connections; everywhere
else it is a recorded input.
_Avoid_: staffing (staffing means which positions the controller AI has been configured to work)

**Wire projection**:
The server-side mapping between a simulation concept and the CRC wire records that carry it. The
projection is the server's; the concept is the simulation's.

**Coast**:
The interval during which a track that has gone away is still displayed before its delete is emitted.
Measured in sim-seconds.
