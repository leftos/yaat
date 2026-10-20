# YAAT-899: why KCOS TAXI A2 C2 drives along 17R/35L

Evidence for Linear YAAT-899 (keep a taxi detour connector off uncleared runway centrelines). The owner ruled that KCOS `TAXI A2 C2` (and `C2 A2`) is a crossing: hold at the entry bar, then on `CROSS` go straight over onto the other taxiway. The aviation review noted that after `CROSS` the aircraft drives about 255 ft along the centreline. This canvas shows why. Open `index.html`; the PNGs sit beside it.

Item = branch yaat-899 with its uncommitted change, including the `cos.json` sidecar that declares A2/C2 a crossing pair of 17R/35L.

## The answer

A2 and C2 do not meet the runway at the same place. In the layout, A2 ends on the centreline at node #12 and C2 ends on it at #40, 255.2 ft further south. Node #12's only other edges run along the runway, so the only pavement from A2 to C2 is 255.4 ft of centreline (#12→#151→#253→#40). At #40 the route turns 149.5° back onto C2, a 30.3° high-speed exit built for 35L arrivals.

C2 then climbs north-east and crosses A2's extended line 150.1 ft east of the centreline, before its own hold bar. That is why the two look as if they face each other. The layout has no pavement along that line.

## Lanes

- **1, route `TAXI A2 C2 C`** (`route-a2-c2.png`): the node chain from A2's bar #84 to C2's bar #90, with the centreline run labelled 255.4 ft and the joins labelled 90.1° (A2) and 30.3° (C2). The C172's drive after `CROSS 35L` is overlaid.
- **2, route `TAXI C2 A2 A`** (`route-c2-a2.png`): the same pavement in reverse, holding at C2's bar #90.
- **3, measured table**: join positions, the along-centreline distance, each bar's distance from the centreline, runway width, and feet inside the runway pavement.
- **4, lanes for the owner** (`lane-a.png`, `lane-b.png`, `lane-c.png`):
  - A, the offset crossing the item makes today, is supported by the layout.
  - B, "straight over", is not supported: there is no taxiway on the 75 ft between the east runway edge and C2 along A2's line.
  - C, refuse unless the instructor names the runway, is supported, with the readbacks it gives.
- **5, side findings**: the crossing route takes no fillets (point turns of 89.9° and 149.5°); the named-runway form pivots at #40 in one direction only; and without the pair, `TAXI C2 A2 A` is refused with the wrong pair ("A2 and A do not connect").
- **6, LayoutInspector's own render** (`li-a2-c2.png`): the same route in the inspector, whose projection stretches east–west.

## Measured versus drawn

Measured, from a temporary test in the item tree (since deleted):

- every node position, edge, arc and runway width, from `TestAirportGroundData`'s KCOS graph;
- every route, readback and hold short, from `GroundCommandHandler.TryTaxi` and `SimulationEngine.SendCommand`;
- the centreline run, from `SegmentExpander.CenterlineLengthFt`;
- the drive, from `SimulationEngine` positions logged every second after `CROSS 35L`.

Lane C's refusal was measured with the sidecar pair hidden by `NavigationDatabase.ScopedOverride(NavigationDatabase.ForTesting())`, without editing `cos.json`.

Computed from those positions in a flat local frame: angles, cross- and along-track distances, and feet inside the runway pavement. "Inside" means within 75 ft of the centreline, from the 150 ft width. The drive's figure joins its one-second positions with straight lines, so it slightly under-counts on curves.

Drawn: the figures are to scale, north up, built from the measured positions. The runway pavement is a 150 ft band along the measured centreline, since the GeoJSON has no pavement outlines. Lane B's dashed line and the shaded 75 ft are the only geometry not taken from the layout, and they are labelled as absent from it. Whether the real KCOS has pavement there was not checked.
