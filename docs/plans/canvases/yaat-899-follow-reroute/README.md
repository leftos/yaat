# YAAT-899: re-staging the follow reroute test

Visual comparison for Linear YAAT-899 (keep a taxi detour connector off uncleared runway centrelines). It shows how the red test `FollowGroundAtRunwayBarLeadTests.FollowingPastOneRight_LeaderReroutedThroughTheFollower_SaysUnableOnce` could be re-staged. Open `index.html`; each lane's PNGs sit beside it.

All times are seconds after `CROSS 1R`. LED1 is the leader (blue), FOL1 the follower (green).

## Lanes

- **Lane 0, base (before YAAT-899)** (`lane0-base.png`): the red test's own flow on base e52b72390 at the driving moment (t=31). `TAXI L F F1 A` is refused ("no room to turn around on LF"). `TAXI F F1 A` is accepted as `LF F P C L F1`, with P, C and L as silent bridges, and the test passes.
- **Lane 1, why named reroutes fail now** (`lane1-why-fail.png`, `lane1-overview.png`): the item tree at t=31, LED1 on LF heading 52. `TAXI L F F1 A` is refused with "F and F1 do not connect" and `TAXI F F1 A` with "Cannot taxi via F". `TAXI A F1 F` and the three named T/B detours are refused for the turn about on LF.

  One second later, `TAXI A F1 F` is accepted as `LF L A F1` and the assertions pass. The sweep finds that acceptance only at t=26, 29, 32 and 35.
- **Lane 2, reroute earlier (round 4 attempt 3)** (`lane2-earlier.png`, `lane2-timeline.png`, `lane2-end.png`): reroute at t=4 is accepted as `L F1`. FOL1 has no follow route until t=31, so it takes the silent "route lost" hold and the test fails.

  The timeline is a per-second snapshot sweep (t=0 to 45, item tree and base). `L F1` is accepted only until t=24, and FOL1 first drives at t=31. The end close-up shows LED1 holding short of 1R on top of FOL1 at t=60.
- **Lane 3, Option A** (`lane3-option-a.png`, `lane3-end.png`): `HOLD` to LED1 on L at t=0, reroute with `TAXI L F F1 A` when FOL1 drives (t=31). Both of the red test's assertions pass.

  Over the next 60 s, outside the test window, LED1 drives F1 into the stopped FOL1 and holds short of 1R on top of it.
- **Lane 4, Option C**: four alternative leader taxis in `StageBehind`. None makes the windows overlap, so the lane is a table with no map.

## Measured versus drawn

Every map, position, route, readback and timeline is measured by a temporary copy of the red test's staging, run on the item tree and on a detached worktree of base e52b72390. Both copies have since been deleted.

The one drawn element is the red line in `lane1-overview.png`. It is round 4's node chain (349, 324, 97, 1218, 1219, 1397, 1395, 152, 66), joined by the shortest layout path per leg, not a route the pathfinder resolved.

The SFO graph is identical in both trees (same node ids, positions, edges and arcs), so all maps share one geometry. In the close-ups the B738s are drawn to scale (129 ft long, 117 ft span).
