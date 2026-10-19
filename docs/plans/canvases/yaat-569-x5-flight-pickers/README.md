Mock for YAAT-569 part X5 (the flight pickers: Fly heading, Turn left / right and the degree turns, Maintain altitude, Assign speed, and the Speed…, Temporary altitude… and Cruise… boxes): the owner decisions the code map `.tmp/map-569-next.md` (section 2 family A, section 4) left open. Open `index.html` in a browser; it works offline and loads no web fonts. It has no image files: every surface is drawn in HTML and inline SVG.

Every flyout draws the built parts: the Fluent dark menu, the approach flyouts' title line and glyph rows, the takeoff and approach flyouts' boxes, `FlyoutTabStrip` tabs, the 480 px cap, the flight glyphs of `QuickCommandGlyphs`. Each decision has lanes A, B (and C) with how it works and its worst case; lane A is the recommended one in every decision and is marked. Cells, rows, chips, tabs and the boxes are live: the line under each lane says what a click or an Enter sends.

The aircraft: UAL455, A320, en route to KOAK at FL240, 280 kt, heading 270, assigned FL240; N738SP, C172, VFR to KOAK at 3,500, 100 kt, assigned 4,000.

A note at the top shows the owner's rulings as settled (the family; the single-box inputs become inline boxes; All Commands opens the same flyout from one builder).

It also recommends, without lanes: the keyboard model (focus in the box on open, Down into the grid or list at the ● mark, arrows by cell and row in the grid, Enter sends, Escape back to the strip button), reusing the strip's flight glyphs, the ruled screen edge and 480 px cap, and the command text (three-digit headings, bare feet for Maintain rows, hundreds for TEMPALT / CRUISE).

- Today: the three popups as built (the 120 px heading list on 270, the Maintain rich list centred on FL240, the speed list on its middle knots value) and the input popup.
- 1. Fly heading layout (all lanes: title line, Heading box on top reading `250` or `L180` / `R090`). A (recommended): a 6 × 12 grid of all 72 headings, one row per 60°, the present heading filled. B: a list of 72 rows (heading, turn from the present heading, command), 300 px scrolling, centred on 270. C: both, the present heading ±15° as rows, then the grid.
- 1b. Present heading (`FPH`). A (recommended): a row at the top of the Fly heading flyout; the All Commands leaf stays. B: not in the flyout; the All Commands leaf only, as today.
- 2. Turns (Turn left / right `TL` / `TR`, degree turns `LT` / `RT`). A (recommended): one flyout with a Fly heading / Turn left / Turn right toggle setting what the grid sends, plus left and right degree chips (5 to 90); each All Commands entry opens it preset. B: one flyout whose grid always sends `FH`, with two columns of relative turns (10 to 90, each naming the heading reached); `TL` / `TR` typed as `L180`. C: separate flyouts per entry, as today's five pickers.
- 3. Maintain altitude in a flyout (the rich list's rows, marks, MVA line and greyed rows kept, a command on every row; the box jumps the list and previews the command). A (recommended): the box on top, the list below in a 300 px panel centred on the assigned row. B: the list, then the box at the foot. C: a nine-row ladder around the assigned row with "Show all n altitudes", the box on top.
- 4. Assign speed (RNS, RFAS, knots, Mach at or above FL240, the Speed (knots) box that replaces Speed…). A (recommended): RNS and RFAS as glyph rows, knots and Mach as chip grids, the present speed filled, box on top. B: one scrolling list of rows as today, box at the foot. C: RNS and RFAS rows over Knots / Mach tabs (the Mach tab only at or above FL240).
- 5. Temporary altitude and Cruise boxes. A (recommended): each Data Block entry opens its own one-box flyout showing the command (`TEMPALT 120`, `CRUISE 240`). B: both boxes in a "Data block" group at the foot of the Maintain altitude flyout.
- 6. While open. A (recommended): live in place every second (marks, commands, MVA line; rows never move). B: static, built at open, as today. C: live only while the pointer is outside the flyout, frozen inside (the ground family's rule). A demo clock plays UAL455 climbing FL240 to FL280 and N738SP climbing 3,500 to 4,000 into a 4,000 ft MVA sector, at 10×.

Data. The altitude rows follow `MenuCatalog.AltitudeValues` and `MaintainList` (every 100 ft to 5,000 ft above the 9 ft field, then every 500 ft; marks, the CM / DM rule, the MVA line and greyed rows), run in the page's script. Heading values are today's 5..360 in 5° steps; degree turns are today's 5, 10, 15, 20, 30, 45, 60, 90. The speed rows follow `SpeedValues`' shape (RNS, RFAS, tenths of knots, Mach at or above FL240).

Flyout sizes quoted in captions were measured from the headless render: lane 1A's flyout 458 × 298 px (the widest of all, under the 480 px cap), lane 1C's 521 px tall, lane 4A's jet flyout 353 px tall, lane 3B's box 363 px below the title line.

Not from real data, or not checked:

- The ceilings (FL390 for the A320, 14,000 ft for the C172) stand in for `AircraftPerformance.Ceiling`; the speed values (A320 knots 140-300 and M.74-M.80, approach speed 136; C172 knots 70-120, approach speed 62) stand in for `SpeedValues`, `KnotsCeiling`, `MachValues` and the profiles' approach speeds. None was run through the code.
- The MVA sectors and floors (6,000 for the jet; 3,000 and 4,000 for the piston, sectors "20" and "21") are illustrative, not read from the MVA data.
- The present Mach (M.66 at FL240 and 280 kt) is an estimate.
- The box parsers are the page's own approximations: the heading box's `L` / `R` prefix, and the altitude box reading hundreds for up to three digits, else feet, as `AltitudeResolver` does.
- Temporary altitude and Cruise have no glyph; decision 5 draws Maintain's step in the scope grey as a draft.

Source files read for it: `.tmp/map-569-next.md`, `.tmp/desc-YAAT-569.md`, and `src/Yaat.Client.Core/ContextMenus/MenuCatalog.cs` (`HeadingList`, `RelativeTurnList`, `HeadingSeed`, `BuildMaintainAltitude`, `MaintainList`, `AltitudeValues`, `BuildAssignSpeed`, `SpeedValues`, the Speed…, Temporary altitude… and Cruise… entries, `BuildCheckedInput`, `FormatAltitude`).

Also read: `src/Yaat.Client/Views/MenuPopups.cs` (`ShowInput`, `ShowList`, `ShowRichList` and the rich row brushes), `QuickCommandGlyphs.cs`, `QuickCommandDefaults.cs`, `TakeoffFlyout.cs`, `FlyoutPanelParts.cs`, `MenuTwoLineRow.cs`, `COMMANDS.md` (Heading, Altitude / Speed, Speed Management), and the mocks `yaat-569-x4-point-menu/`, `yaat-569-x3-approach/` and `yaat-569-x3b-verbs/`.
