# YAAT-899: the bridge filter's suspected false refusals

Evidence for Linear YAAT-899 (keep a taxi detour connector off uncleared runway centrelines). The C# review suspected that the item's new bridge filter refuses three kinds of clearance with "Unable, X and Y do not connect, request taxi instructions" when it should not. This canvas shows real examples of each, found by scanning the committed test layouts. Open `index.html`; the PNGs sit beside it.

Item = branch yaat-899 with its uncommitted change. Base = e52b72390. Every verdict is `GroundCommandHandler.TryTaxi` with a B738 held in position at the named start node and heading, run on both trees.

## Lanes

- **Summary table**: each case's verdict and how often it occurred. The scan covered 16 airports, 19,939 probes and 6,499 refusals. SJC, FAT, HWD, MER and RNO could not be searched: their TestData layouts are runway-only stubs that parse to 0 nodes.
- **Case 1, legal bridge dropped with the cheapest** (`c1-sfo-z1-s2.png`, `c1-mia-q1-m1.png`, `c1-sfo-a2-b.png`): SFO `TAXI Z1 S2`, MIA `TAXI Q1 M1` and SFO `TAXI A2 B` are refused by the item.

  A bridge the item's own rules allow (green) reaches the same node of Y as a dropped one (red): Z1 S3 (889 ft), Q1 M1L (818 ft), A2 M1 (944 ft). Naming the stub (`TAXI Z1 S3 S2` and so on) resolves in both trees. The base accepted each clearance but held short of an unnamed letter.
- **Case 2, junctions exist but "do not connect"** (`c2-lax-e-w.png`, `c2-cmh-b-b1.png`, `c2-cmh-sr-c1.png`): LAX `TAXI E W` with both E/W junctions behind the aircraft, and CMH `TAXI B B1` and `TAXI SR C1`, where the walk along X dead-ends short of the junction ahead.

  The item says the pair does not connect, but each pair has 1 or 2 junctions. The base resolved each pair badly: 580 ft along 06R/24L, and an 11,929 ft loop across 10R/28L twice.
- **Case 3a, the runway the aircraft stands on** (`c3-oak-d-g.png`, `c3-mia-z-m.png`): OAK `TAXI D G` from 15/33 and MIA `TAXI Z M` from 12/30. Every bridge starts with 33 or 61 ft of the runway the aircraft is on, a runway the search itself allows, and every bridge is dropped. The base accepted both.
- **Case 3b, stitched crossing** (`c3-cmh-e4-f3.png`): CMH `TAXI E4 F3`, a 124 ft crossing of 10L/28R through `:link` edges. The offset-crossing rule did not take it, and every bridge was dropped as running along the runway. No example was found for the review's MIA S over 12/30.
- **How the scan was run**: the probe kinds, the legal-bridge oracle and what "rebuilt" means.

## Measured versus drawn

Measured: every verdict, readback, drop count and drop reason, junction count, `[local]` walk failure, and base route with its length and runway feet. Also every legal-bridge node chain, found by AutoRouter on the item's own search context with unnamed letters and runway edges hard-excluded. The maps are drawn from the layout's own node positions and edges, identical in both trees.

The red dashed "dropped bridge" in each map is rebuilt, not captured. The item's diag gives only the dropped node and the reason. The route is AutoRouter's cheapest route to that node on the same context, without the item's `startOverride`, so it shows the kind of route dropped and why, not necessarily the item's exact node chain.

The temporary test file and the base worktree have been deleted.
