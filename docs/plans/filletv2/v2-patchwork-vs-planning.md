# Fillet V2 — patchwork fixes vs plan-then-execute

Notes from a 3f parity session: whether recent V2 connectivity work is drifting toward legacy-style post-hoc repairs instead of fixing planning.

## Question

Are we starting to drift towards the patchwork post-hoc fixes that Legacy had to do, instead of planning better?

## Answer

**Yes** — several changes from the OAK/SFO/FLL parity push are the same *class* of fix as legacy, even if they live under V2 names.

### What V2 was supposed to avoid

The design in [`v2-implementation.md`](./v2-implementation.md) is explicit: **plan everything, execute literally**, and keep the normalizer to distance/adjacency + a defensive coincident merge. Legacy’s post-hoc passes exist because planning and execution were intertwined:

| Legacy repair | V2 additions (3f session) |
|---------------|---------------------------|
| Phase D3 reconnect (parking → filleted junction) | `ReconnectOrphanedEdges` |
| `RescueOrphanedTangentNodes` | `RescueArcOnlyV2TangentNodes` in normalizer |
| Invented shorten/bypass edges | `arm-bypass`, `corner-connector`, `tangent-pair` |
| “Find any cut / any neighbor” heuristics | `FindCutForCornerArm` fallback, tangent clique idea |

Those are **connectivity patches after a plan that didn’t promise connectivity**, not planner improvements.

### What’s still legitimate

A few items are not the same bucket:

- **Stable-node BFS in the harness** — comparison fix (legacy and V2 assign different tangent IDs; comparing full node-id sets was the wrong metric).
- **Normalizer pruning** (dangling refs, degenerate intersection edges, isolated nodes) — hygiene after execute, similar to “recompute distances.”
- **Defensive coincident merge** — already in the spec as a backstop.

### The real gap (planning, not executor band-aids)

Parity failures (`only-legacy` stable nodes: ~51 FLL, ~146 OAK, ~229 SFO at time of discussion) line up with **plan drops**, not random executor bugs:

- Hundreds of `SINGLE_CUT_REJECTED` / `CORNER_DEMOTED` / `NO_OWNING_CUT`
- Far fewer V2 arcs than legacy (~half on OAK/SFO)
- Parking gate broke when reconnect targeted the junction node that was about to be deleted (symptom of “fix in execute,” not “op in plan”)

So the graph is often **under-specified**: the plan removes junction topology and arm roots, but doesn’t always emit explicit ops for:

1. Every **non-arm edge** into the junction (parking, spurs, etc.) → should be `ReconnectEdgeOp` / `PreserveStubOp` in the plan.
2. Every **surviving corner** (including demoted-to-straight) → either `CornerArcOp` or an explicit `StraightConnectorOp` between named cuts.
3. **Arm with no cuts** at a processed junction → explicit `ArmBypassOp`, not “if cuts.Count == 0, add edge.”
4. **Tangent chain continuity** after merges → planned chain edges, not arc-only tangents + rescue.

Until the plan is **connectivity-complete**, executor/normalizer patches will keep multiplying and we’ll recreate legacy with a different file layout.

### Recommended direction

1. **Stop adding** reconnect/rescue/clique/connector heuristics in the 3f push.
2. **Extend the plan model** so `FilletPlan` carries reconnects, straight connectors, and arm bypasses as first-class ops (resolver/builder owns them).
3. **Tighten the resolver** so demotion/rejection either keeps a straight connector in the plan or doesn’t consume that junction’s topology.
4. Keep the harness on **stable pre-fillet node reachability**; use soft gates (corner buckets, warnings) for radius deltas already documented in [`v2-divergences.md`](./v2-divergences.md).

### Next step (when resuming 3f)

See [`v2-pass-6-connectivity-ops.md`](./v2-pass-6-connectivity-ops.md): revert patchwork execute/normalizer changes, commit harness only, implement `ReconnectEdgeOp` / `StraightConnectorOp` / `ArmBypassOp` in the planner, keep executor literal.

### Process rule (harness)

The harness exists to **find planner gaps**, not to be made green. Patches in `FilletPlanExecutor` or `FilletGraphNormalizer` signal under-specified plans — name the missing op instead of patching at the site.
