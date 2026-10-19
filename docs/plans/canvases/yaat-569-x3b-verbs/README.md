Mock for YAAT-569 part X3b: the six approach verbs that join Cleared approach and Expect approach on the tabbed approach flyout built in X3a (commit 55a0d2a5b). Open `index.html` in a browser; it works offline and loads no web fonts. It has no image files: every surface is drawn in HTML and inline SVG.

The six are Join approach (`JAPP {id}`), Cleared straight-in (`CAPPSI {id}`), Join straight-in (`JAPPSI {id}`), Cleared approach (force) (`CAPPF {id}`), Join approach (force) (`JAPPF {id}`) and Join final approach course (`JFAC {id}`), one flyout per verb, the verb fixed by its All Commands entry. Already ruled: none of the six has visual rows, the untowered airport row or the Force toggle, and the force pair's rows are always forced, with the force mark.

Every flyout draws the built X3a panel (`ApproachFlyoutPanel`): the default row pinned first with keyboard focus, a separator, the runway tabs with each tab's row count, the selected tab's rows, a separator and the Approach box.

The situation: UAL455, A320, IFR arrival to KOAK, runway 30 assigned and expecting ILS 30, so every default row is ILS 30 ("Join ILS 30", "Cleared straight-in ILS 30", "Join straight-in ILS 30", "Cleared ILS 30 (force)", "Join ILS 30 (force)", "Join final course ILS 30", the old leaf labels) and the flyout opens on the 30 tab.

Each decision has lanes A, B and C, with how each works and its worst case; no lane is marked recommended. Rows, tabs and boxes are clickable.

1. Does each flyout name its verb in a title line. Today only Cleared approach has one (it holds the Force toggle); Expect approach has none. In All Commands, Join straight-in and Cleared approach (force) are neighbours, and a slip opens a flyout whose focused default row sends `CAPPF I30` on Enter.

   Each lane lists the first line of all eight flyouts, then draws Join approach, Cleared approach (force) and Join final approach course; the six's rows show an empty dashed glyph slot (decision 2 is open).
   - A: no title line on the six, as Expect approach.
   - B: a title line naming the verb on all eight flyouts, the force pair's with the force mark beside the title.
   - C: a title line only on the force pair, with the force mark; the other four open on their default row.
2. Which glyph each verb's rows carry. The panel shows the verb's glyph on every row and on the Approach box, and Join, straight-in, force and JFAC have none. Each lane shows the eight verbs at strip size (40 px cells) and row size (22 px), then the same three flyouts, drawn without the six's title lines (decision 1 lane A).
   - A: CAPPSI and CAPPF reuse the Cleared approach glyph; JAPP, JAPPSI, JAPPF and JFAC share one new Join glyph (a track curving onto the final approach course).
   - B: one glyph per family: Join (the curving track), straight-in for both CAPPSI and JAPPSI (a straight track onto a runway bar), JFAC (a level track above the runway: the course without the descent), and each force verb its base glyph (Cleared or Join) with an amber "!" disc in the lower right.
   - C: no glyph on the six; the row starts with the approach name.

Data. KOAK approaches come from the bundled CIFP (`tests/Yaat.Sim.Tests/TestData/FAACIFP18.gz`) as the X3 mocks list them: 30 (I30, L30, R30-Y, H30-Z), 28R (I28R, L28R, R28RY, H28RZ), 10R (R10R, S10R), 12 (I12, L12, R12-Y, H12-Z), 28L (R28LY, H28LZ), 10L (R10L).

Without visual rows, 15 and 33 have no approach and get no tab (`ApproachFlyoutModel.TabsFor`). The tabs follow `RunwaysFor`: the room's arrival ends (30, 28R) first, then the rest, landable before short. 10L (5,336 ft available) is short for an A320 (4,724 × 1.15 = 5,433 ft), so its row is dimmed "short".

Not from real data, or not decided:

- The callsign, the aircraft state and the room's runway setup (KOAK landing 30 and 28R) are illustrative; the place of 10R, 12 and 28L among the rest follows `visuals.html`'s order and is not checked against `GetPatternRunwayChoices`.
- The Join, straight-in and JFAC glyphs and the glyph force mark are drafts for this decision, in the `QuickCommandGlyphs` style (24 px view box, stroke 1.9, round caps and joins, flight blue `#A8CAFF`); the "!" disc is ringed in the surface colour to part it from the strokes. They are not in the approved icon set.
- The six's title lines are drawn as `ApproachFlyoutPanel.TitleLine` draws Cleared approach's (11 px `#A4ADB8`), with an empty right column; the force pair's title carries the row force badge.
- Lane C of decision 2 needs `ApproachFlyoutPanel` to accept a verb with no glyph: today it throws (`QuickCommandGlyphs.For(verbId) ?? throw`).
- Cancel IFR is left out of every flyout: no ruling says whether the six offer it.

Source files read for it: `src/Yaat.Client.Core/ContextMenus/ApproachFlyout.cs` (the panel, the title line, the glyph by verb id), `ApproachFlyoutModel.cs`, `QuickCommandGlyphs.cs`, `MenuCatalog.cs` (`ApproachPickerIds`, the old leaf labels), `SharedMenuGroups.cs` (`Approach`), and the mocks `docs/plans/canvases/yaat-569-x3-approach/` (`index.html`, `visuals.html`, `force.html`, `README.md`) and `docs/plans/canvases/quick-command-icons/`.
