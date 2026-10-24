Mock for YAAT-1048 (Cleared visual offers only visuals, one per available runway): three owner decisions the ruling "'cleared visual' should offer only visuals for all available runways, not other approaches" leaves open. Open `index.html` in a browser; it works offline and loads no web fonts. It has no image files: every surface is drawn in HTML and inline SVG.

Today the Cleared visual entry opens the whole Cleared approach flyout (`ClearedVisualSpec` is the Cleared approach spec with `OpensOnVisual`): runway tabs, each a `Visual RWY n` row then that runway's instrument approaches, a Circling tab, a pinned instrument default, and the Approach box sending `CAPP {typed}`; it only opens on the visual row.

Already decided and drawn in every lane: no instrument rows, no Circling tab, no pinned instrument default; the Force toggle in the title line stays (rows then send `CVAF {rwy}` and wear the built force mark, and Force lifts the greying); Cancel IFR… (`CIFR`) stays, at the bottom as built for a destination with instrument approaches; a visual row is greyed "field not in sight" (not clickable, skipped by the arrow keys, the sim's refusal as its tooltip) until the pilot reports the field. The untowered-field "Visual approach to {airport}" row stays where it applies; KOAK is towered, so it is not drawn.

Each decision has lanes A, B and C, with how each works and its worst case; no lane is marked recommended. Rows, tabs, the Force toggle and the "Other runways…" row are clickable, and the boxes take typing.

1. Layout and title (every end gets a row, as decision 3 A; no box, as decision 2 A). Each lane is drawn with the field reported in sight (the first live row focused) and not reported (every row greyed, nothing focused).
   - A: the runway tabs kept, each tab holding its one visual row (every tab reads "n 1"), title "Cleared approach" as today; also drawn with the 15 tab chosen (short and greyed) and with Force ticked.
   - B: one flat list, one row per runway end (`Visual RWY 30 · CVA 30`), in the built `RunwaysFor` order, title "Cleared visual approach".
   - C: the flat list of B with a separator between the room's landing ends (30, 28R) and the rest.
2. The typed box (drawn on decision 1 B, field in sight).
   - A: no box.
   - B: a "Runway" box (hint "runway, e.g. 28R") that filters the rows by runway prefix and sends `CVA {typed}` (`CVAF` with Force) once the text names a runway end; drawn empty, with 28R typed, and with 27 typed, refused as "KOAK has no runway 27".
   - C: today's Approach box (hint "id, e.g. I28R") sending `CAPP {typed}` (`CAPPF` with Force); drawn empty and with I28R typed.
3. Which runways get a row (drawn on decision 1 B, field in sight, no box).
   - A: every runway end (today's `RunwaysFor`): the room's landing ends first, then the rest, landable before short; 15 and 33 dimmed "short" and clickable.
   - B: only the room's landing (active arrival) ends, 30 and 28R; also drawn for a room with no landing runways set (an empty list with a line).
   - C: the landing ends, then an "Other runways… · 6" row that unfolds the rest in place; drawn folded and unfolded.

Data. UAL455, an A319, IFR arrival to KOAK, no assigned runway and no expected approach, so the flyout opens on the first runway. KOAK runway ends are NavData's: 10L, 10R, 12, 15, 28L, 28R, 30, 33. An A319 has a 4,429 ft landing distance (`src/Yaat.Sim/Data/AircraftProfiles.json`), so it needs 5,093 ft (× 1.15): 15 and 33 (3,376 ft) are short, and every other end (5,336 ft or more, as the x3 README lists them) passes. The row order is the built `RunwaysFor` order: the room's arrival ends in the room's order, then the rest in the pattern-entry order, landable before short (10L, 10R, 12, 28L, 15, 33, as `visuals.html` orders them). KORD's 16 runway ends (decision 1 B's worst case) come from the x3 README; 16 rows at 32 px is 512 px against the 420 px `RowsMaxHeight` scroller.

Not from real data, or not decided:

- The callsign's type, the aircraft state and the room's runway setup (KOAK landing 30 and 28R) are illustrative.
- In decision 1 B the order is the built one (landing ends first), so B and C differ only by C's separator; a B in plain runway-number order was not drawn.
- The flat lists, the "Cleared visual approach" title, the Runway box with its prefix filter, its refusal line and its greyed rule (the box sends nothing while the rows are greyed, unless Force is ticked), the "Other runways…" row and the empty-room line ("No landing runways set for KOAK in this room.") are drawings of the options, not built UI. What decision 3 B or C show when the room has no landing runways set is not decided.
- Decisions 2 and 3 are drawn on decision 1 B only; their lanes on the tabbed or grouped layout are described in the artboards' intros, not drawn.
- The tab label count ("30 1") follows the built `TabsFor` label (`"{runway} {rows.Count}"`). The built tab strip has no divider between the room's runways and the rest, so none is drawn.
- The aircraft menu stub's quick strip (Cleared approach, Cleared visual latched, Maintain altitude, Assign speed, Report field in sight) is illustrative; the Cleared visual cell carries the existing eye glyph.
- The "Other runways…" row's height cost (about 190 px unfolded at KOAK) is six 32 px rows, not measured in Avalonia.

Source files read for it (on `feat/context-menu-flyouts`): `src/Yaat.Client.Core/ContextMenus/ApproachFlyout.cs` (title line and Force toggle, pinned rows, box, Cancel IFR placement, `OpeningFocus`), `ApproachFlyoutModel.cs` (`TabsFor`, `RunwaysFor`, `VisualRow`, `Waiting`, `Command`, the row notes), `MenuCatalog.cs` (`ClearedVisualLabel`), `TaxiToRunwayFlyout.cs` (`RowsMaxHeight`), `src/Yaat.Sim/Data/AircraftProfiles.json`, the code map `.tmp/map-1067-1048.md` (Item B), and the mocks `docs/plans/canvases/yaat-569-x3-approach/` (`visuals.html`, whose styles this page starts from, and `force.html`).
