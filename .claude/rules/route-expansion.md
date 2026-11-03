---
paths:
  - "src/Yaat.Sim/Data/RouteExpander.cs"
  - "src/Yaat.Sim/Data/NavigationDatabase*.cs"
  - "src/Yaat.Sim/Data/Vnas/**"
---

# Route expansion: RV-SID synthetic transitions

`NavData.dat` encodes radar-vectors SIDs (e.g. NIMI5/OAK6) with synthetic `[OAK, X]` "transitions" that are not published CIFP transitions.

Flight-plan callers must pass `RouteExpander.Expand(..., includeAllTransitionsOnMismatch: false)` so the emit-all fallback doesn't fabricate a turn-back through every X fix; only autocomplete/UI paths pass `true`. See `docs/navigation-database.md` for the full caller table and route-expansion internals.
