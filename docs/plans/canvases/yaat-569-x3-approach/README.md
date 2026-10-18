Mock for YAAT-569 part X3 (the Cleared approach / Expect approach designed flyout): six owner decisions the ruled first-batch lane B left open. Open `index.html` in a browser; it works offline and loads no web fonts. It has no image files: every surface is drawn in HTML and inline SVG.

Every lane draws the ruled lane B of `docs/plans/canvases/quick-command-flyouts/quick-command-flyouts.html` (section 8): a runway tab bar (runway and approach count), the chosen runway's approaches as glyph rows (procedure name, kind, command), a separator and the "Approach" box sending `CAPP <id>` / `EAPP <id>`. Each decision has lanes A, B and C (two for decision 5), with how it works and its worst case under each; no lane is marked recommended. Rows, tabs and boxes are clickable, the box takes typing, and decision 1 lane C's verb selector works.

1. Which verbs use the flyout (UAL455 to KOAK, no default, 28R tab chosen).
   - A: only Cleared approach and Expect approach get the flyout; Join, both straight-ins, both forces and JFAC keep today's nested runway submenus in All Commands (drawn: All Commands with flyout/nested pills, the CAPP flyout, the JAPPF nested cascade).
   - B: all eight verbs get the same flyout, one per All Commands entry; a force flyout has an amber title, a filled "force" badge on every row and amber commands; JFAC's flyout is titled "lateral only, no descent".
   - C: one "Approach clearance" flyout with a Verb row (Cleared · Join · Expect · Final course) and a Kind row (Normal · Straight-in · Force) above the tabs, drawn with Cleared + Force.
2. Where a circling approach goes (EJA512 to KTRK).
   - A: a last "Circling" tab. B: a "Circling · any runway" section under every runway tab. C: left out; reached only by typing the id in the box.
3. The strip's default approach (UAL455 to KOAK, runway 30 assigned, expecting ILS 30).
   - A: the strip button stays a one-click leaf (`CAPP I30`); "Cleared approach (other)…" opens the flyout, whose first row is the default "Cleared ILS 30 · default". B: the strip button always opens the flyout, the default row first and focused, no "(other)" row. C: leaf stays; the "(other)" flyout has no default row (also drawn with 28R assigned, where it opens on 28R and the expected ILS 30 is a tab away).
4. Opening tab and order with no default runway (UAL455 to KOAK, nothing assigned or expected).
   - A: the assigned runway's tab first and selected, else runway order with the first (10L) selected. B: no tab selected, a "Choose a runway" hint until a tab is clicked. C: tabs ordered by the room's runway configuration (landing runways first, as `PatternRunwayChoices.ActiveArrivalEnds` orders the pattern entries), the first selected.
5. A destination with no approaches (N738SP to KHES).
   - A: keep today's "Approach ID" popup (`MenuCatalog.BuildInput` → `MenuPopups.ShowInput`). B: a flyout with only a dimmed "No approaches at KHES" line and the box.
6. A tab bar wider than 480 px (AAL1234 to KORD, 27L assigned, opened from "(other)").
   - A: wrap (two rows at 480 px). B: one row with ‹ › scroll arrows, opened scrolled to the selected tab. C: compact labels: each runway number once, its parallels as one-letter tabs (27: C L R); all 15 fit one row.

Data. Approach ids, runways and procedure names come from the bundled CIFP, `tests/Yaat.Sim.Tests/TestData/FAACIFP18.gz` (approach records, section P subsection F; runway records, subsection G), named as `ApproachPickerBuilder` names them (`FullName`, kind order ILS, LOC, RNAV, RNP, others; runways de-padded by `RunwayIdentifier.ToDisplayDesignator`).

- KOAK: 17 approaches on 10L, 10R, 12, 28L, 28R and 30 (15 and 33 have none); no circling approach.
- Circling: KTRK (Truckee) has `R11`, `R20` and the circling `RNV-A` (RNAV (GPS)-A); runways 2 and 29 have no approach. The bundled CIFP's other nearby circling approaches are `VOR-A` at KVCB (Nut Tree, with `R20`) and at KWVI (Watsonville, with `L02`, `R02`).
- No approaches: KHES (Healdsburg, runways 13/31) has no approach record.
- Wide tab bar: KORD has 16 runway ends, 15 with an approach (4L has none): 4R 9C 9L 9R 10C 10L 10R 22L 22R 27C 27L 27R 28C 28L 28R; 10R has six (`I10RY`, `I10RZ`, `L10RY`, `L10RZ`, `R10RY`, `R10RZ`). KDFW has 13 ends with an approach.

Not from real data, or not checked:

- Callsigns, types, altitudes, speeds and the room's runway configuration (KOAK landing 30 and 28R in decision 4 C) are illustrative.
- The tab's approach count is drawn as the ruled lane B draws it; the built `FlyoutTab` carries only a label and a state, so the count would be new.
- Join, straight-in, force and JFAC have no glyph (`QuickCommandGlyphs`); their rows show an empty dashed glyph slot. The force and "lateral only" badges, the amber force title and commands, the Circling tab, the circling section, the verb selector, the "No approaches" line, the hint line and the compact tab groups are drawings of the options, not built UI.
- Whether `CAPP RNV-A` resolves and flies in the sim was not checked.
- KORD lane C's one-row fit is measured only in this page's headless render (Chromium, local fonts), not in Avalonia.

## Visual approaches and Cancel IFR (`visuals.html`)

A second mock, after the owner ruled visual rows on Cleared approach and Expect approach, the airport-level `CVA {airport}` row at an untowered field and a confirmed Cancel IFR (`CIFR`); it asks how they look. Open `visuals.html` beside `index.html`; it is drawn in HTML and inline SVG, with no image files.

Every flyout draws the ruled design (room-order tabs, the first selected, wrapping inside 480 px). Each decision has lanes A, B and C, with how each works and its worst case; no lane is marked recommended.

1. Where the visual rows sit in Cleared approach (GTI3591, B744, to KOAK; the room lands 30 and 28R).
   - A: a `Visual RWY n` row (`CVA n`) at the top of each runway tab; 15 and 33, which have no approach, get tabs holding only their visual row.
   - B: a last "Visual" tab listing every runway's visual row in the pattern-entry order (room runways, separator, the rest; landable first); runway tabs keep instrument approaches only.
   - C: a "Visual" section (with the runway's landing distance available) at the bottom of each runway tab.
2. The same for Expect approach, the rows sending the placeholder `EAPP VIS<runway>`.
3. An untowered field with no instrument approach (N738SP, C172, to KHES).
   - A: only the `CVA KHES` row and Cancel IFR…. B: the same with a dimmed line saying why there are no runway rows. C: Cancel IFR… first, then the `CVA KHES` row.
4. The Cancel IFR confirmation, each lane also drawn with the weather line ("Reported weather is below VFR minimums.").
   - A: an inline strip under the row with the question, Confirm and Back. B: a small dialog, the menu closed, as the Approach ID popup opens. C: a two-click row that arms as "Confirm cancel IFR" with the question under it.

Data. Approaches from the bundled CIFP, as in `index.html`. Runway lengths are NavData's, read through `RunwayLandability.LandingDistanceAvailableFt` (declared landing distance): KOAK 12 and 30 10,000 ft, 10R and 28L 6,213, 28R 5,457, 10L 5,336, 15 and 33 3,376.

A B744 has a 6,988 ft landing distance (`AircraftProfiles.json`), so it needs 8,036 ft (× 1.15): only 12 and 30 pass `IsLandable`, and the other six runways' visual rows are dimmed "short". KHES (13 and 31, 2,652 ft) has no approach record in the bundled CIFP, only two STAR records (REBAS1, VNYRD1); a C172 needs 603 ft (524 × 1.15).

Not from real data, or not decided:

- Callsigns, aircraft states, the room's runway setup (KOAK landing 30 and 28R) and the weather (KHES drawn as if reporting OVC008 2SM) are illustrative. Whether KHES is untowered comes from outside the data: the sim has no towered/untowered fact yet (the sim sub-issue adds it).
- `EAPP VIS28R` is a placeholder for the expect-visual token. The dashed eye on Expect visual rows, the "Visual" tab, the "Visual" section title, the untowered note, the confirm strip, the dialog and the armed row are drawings, not built UI. Cancel IFR has no glyph (an empty dashed slot).
- The tab counts in lanes A and C count the visual row; the KHES flyouts leave out the Approach box (nothing to type there), which no ruling covers. Section 4 draws Cancel IFR first, as the orchestrator decision places it.
- The confirm strip's and armed row's heights quoted in the worst cases are measured in this page's headless render (Chromium, local fonts), not in Avalonia.

Source files read for it: `src/Yaat.Sim/Phases/RunwayLandability.cs`, `src/Yaat.Client.Core/ContextMenus/PatternRunwayChoices.cs`, `MenuCatalog.cs` (`BuildPatternRunwayRow`), `MenuGlyphRow.cs`, `QuickCommandGlyphs.cs`, `COMMANDS.md` (`CVA`, `CIFR`), the YAAT-569 rulings and orchestrator decision, `src/Yaat.Sim/Data/AircraftProfiles.json`, and a throwaway probe over `TestVnasData` for the runway figures.

## Sources for `index.html`

Source files read: `.tmp/map-569-x3.md` (code map and open decisions), `src/Yaat.Client.Core/ContextMenus/ApproachPickerBuilder.cs`, `MenuCatalog.cs` (`ApproachSpec`, `DestinationApproaches`), `SharedMenuGroups.cs` (`Approach`), `FlyoutTabStrip.cs`, `TaxiToRunwayFlyout.cs`, `ExitFlyout.cs`, `IMenuHost.cs` (`GetPatternRunwayChoices`), `RunwayDesignatorComparer.cs`, `src/Yaat.Client/Views/MenuPopups.cs` (`ShowInput`), `COMMANDS.md` (Approach Control Commands), and the mocks `quick-command-flyouts/`, `quick-command-taxi-to-runway-tabs/` and `quick-command-exit-passed/`.
