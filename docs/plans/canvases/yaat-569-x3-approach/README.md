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

Source files read: `.tmp/map-569-x3.md` (code map and open decisions), `src/Yaat.Client.Core/ContextMenus/ApproachPickerBuilder.cs`, `MenuCatalog.cs` (`ApproachSpec`, `DestinationApproaches`), `SharedMenuGroups.cs` (`Approach`), `FlyoutTabStrip.cs`, `TaxiToRunwayFlyout.cs`, `ExitFlyout.cs`, `IMenuHost.cs` (`GetPatternRunwayChoices`), `RunwayDesignatorComparer.cs`, `src/Yaat.Client/Views/MenuPopups.cs` (`ShowInput`), `COMMANDS.md` (Approach Control Commands), and the mocks `quick-command-flyouts/`, `quick-command-taxi-to-runway-tabs/` and `quick-command-exit-passed/`.
