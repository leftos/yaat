Mock for YAAT-569 part F (runway, STAR and procedure pickers): the menu entries that still end in the list popup or a bare text box because they pick a runway or a STAR. Converting them retires `IMenuHost.ShowListPopup`.

The family: Cleared visual approach (`CVA {rwy}`) and its "(other)…" list; Join STAR (`JARR {star}`) and its "(other)…" list; the no-data text boxes of those two and of the five pattern entries (Enter left/right downwind, left/right base, Make straight-in); and the Custom… row of Cleared for takeoff (blank sends `CTO`, else `CTO {text}`). Today's behaviour is `src/Yaat.Client.Core/ContextMenus/MenuCatalog.cs` :753-806, :1282-1287, :1630-1714.

Open `index.html` in a browser; it works offline and loads no web fonts. It has no image files: every surface is drawn in HTML and inline SVG.

## How to read the page

The page opens with what earlier parts settled (drawn as given, not as lanes) and what was found in the source while drawing. Then comes "Today", the surfaces as built, followed by one artboard per state. In each artboard the lanes sit side by side as columns, each lane with a line saying what a click or Enter sends, how it works and its worst case in use. Rows, tabs, boxes and Force toggles are live.

Two page switches: Field reported in sight (off greys the visual rows "field not in sight" until Force is on) and Blank Custom sends `CTO` (decision 5).

- 1a Cleared visual with a default runway (UAL455, A320, IFR to KOAK, 28R assigned; the room lands 30 and 28R).
- 1b Cleared visual with no default.
- 1c Cleared visual at an untowered field (N4417Q, C182, IFR to KHAF), with the airport-level `CVA KHAF` row; KOAK in 1a and 1b is the towered contrast, where `CVA OAK` is refused.
- 2a Join STAR with a filed STAR (SWA1957, B737, route files OAKES3).
- 2b Join STAR with none filed.
- 3 The no-data state, two options side by side, for Cleared visual (N22RX, no destination), Join STAR (SKW5321 to KLVK, no STARs) and Enter left downwind (N52TW, no flight plan).
- 4 Cleared for takeoff's Custom row (UAL1517, A320, IFR, holding short of 30): one treatment and a variant.

## The decisions, numbered as on the page

1. Lane.
   - A: one flyout per verb, the default pinned first and focused, every other choice below, no "(other)" companion.
   - B: one list with the default highlighted where it falls (closest to today, restyled as a flyout).
   - C: Cleared visual opens the built Cleared approach flyout on the default runway's Visual row; Join STAR becomes a tabbed STAR flyout (a tab per STAR, rows for nearest fix ahead, each entry fix and each runway transition, Descend via STAR under the tabs).
2. "(other)…" companions: a drop them (one entry, the flyout holds every choice) or b keep an All Commands-only "(other)…" opening the same flyout. The pattern entries' own "(other)" flyout (`SharedMenuGroups.cs:683`): c the same answer, or d leave it.
3. No-data state: a a box in the flyout under a line saying why (blank sends nothing for Cleared visual and Join STAR, the bare verb for a pattern entry, as today), or b nothing to pick (inert strip button, disabled All Commands entry, the command bar for the rest).
4. CTO Custom row: A an inline box row, always open, `CTO` fixed before the box; B a Custom… row that opens its box in place.
5. Blank Custom: a sends nothing (the flyout's first row already sends `CTO`), or b sends `CTO`, as today.
6. Runway rows in lanes A and B: a the pattern-entry order (room runways first, short ones dimmed last), or b runway order, unmarked (today's list).
7. Field in sight and Force in lanes A and B: a as the approach flyout (greyed until `RFIS`, a Force toggle sending `CVAF`), or b neither (rows always live, the sim refuses).

## Data

From the bundled CIFP (`tests/Yaat.Sim.Tests/TestData/FAACIFP18.gz`):

- KOAK STARs: AANET1 (RBUCL, SPAMY; RW12), EMZOH4 (RGOOD; RW12, RW28B, RW30), OAKES3 (MRLET, RPARK, RUSME; RW12, RW28B, RW30), PIRAT3 (ALCOA, CINNY, HUNTS, PAINT, PASIF, WUSES), PXN6 (AVE, GMN), WNDSR2 (RBUCL, SPAMY; RW28B, RW30). RW28B serves 28L and 28R.
- KOAK approaches: I/L/R…Y/H…Z on 30, 28R and 12; R28LY and H28LZ on 28L; R10R and S10R on 10R; R10L on 10L; none on 15 and 33.
- KHAF: runways 12 and 30, approaches R12 and R30, no STAR. KLVK: runways 7L/7R/25L/25R, approaches I25R, L25R, R25R, no STAR. KSFO has 13 STARs.

Runway order and short runways follow `RunwayDesignators.ForAirport` (number, then suffix) and the X3 visuals mock's landing distances for an A320 (5,433 ft needed: 10L 5,336, 15 and 33 3,376 are short).

## Not from real data, or not checked

- Callsigns, aircraft states, routes and the room's runway setup (KOAK landing 30 and 28R; KHAF untowered) are illustrative.
- The Join STAR rows' transition notes, the tabbed STAR flyout, the "nearest fix ahead" wording, the no-data lines, the inline Custom box with its fixed `CTO`, and the "· no runways known" notes are drawings of options, not built UI. Join STAR has no glyph, so its rows show an empty dashed slot; the takeoff glyphs are drawn unrotated.
- Whether a bare `ELD` with no airport does anything in the sim was not checked. `JARR {star} {fix}` and `JARR {star} {rwy}` are from COMMANDS.md, not run.
- The findings on the page (the first list entry cannot be clicked, Cleared visual offered to VFR aircraft) are static reads of the source, not reproduced in the client.

Source files read for it: `MenuCatalog.cs`, `SharedMenuGroups.cs`, `AircraftMenuBuilder.cs`, `ApproachFlyoutModel.cs`, `ApproachChoices.cs`, `TakeoffFlyout.cs`, `PatternRunwayChoices.cs`, `RunwayDesignators.cs`, `QuickCommandGlyphs.cs`, `QuickCommandDefaults.cs`, `src/Yaat.Client/Views/MenuPopups.cs`, `tests/Yaat.Client.UI.Tests/Views/MenuPopupsTests.cs`, `docs/client-context-menus.md`, COMMANDS.md (`CVA`, `JARR`, `CTO`), and the mocks `yaat-569-d-traffic/`, `yaat-569-x3-approach/` and `takeoff-flyout/`.
