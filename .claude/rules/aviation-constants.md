---
paths:
  - "src/Yaat.Sim/AircraftCategory.cs"
  - "src/Yaat.Sim/Phases/Ground/**"
---

# Validated aviation constants

- The category values here (`CategoryPerformance` speeds, rates and snap thresholds; the taxi, expedite, tug, turn-about and takeoff-roll rates) are validated by `aviation-sim-expert` against FAA 7110.65 / AIM.
- A change to one goes through the `aviation-review-gate` skill.
- The table is `docs/flight-physics.md` "The validated category table"; the ground rates are in `docs/ground/navigator.md`, `docs/ground/pushback.md` and `docs/landing-and-runway-exit.md`.
