Mock for YAAT-569 part D (traffic): the aircraft menu's Report traffic in sight… and Follow traffic…, which the owner ruled become one traffic-picker flyout family.

Today Report traffic in sight… opens a rich-row popup of the five nearest airborne aircraft (with none, a bare text box) and Follow traffic… a bare text box (`src/Yaat.Client.Core/ContextMenus/MenuCatalog.cs` :171, :409-415, :1360-1423). Open `index.html` in a browser; it works offline and loads no web fonts. It has no image files: every surface, the radar excerpt included, is drawn in HTML and inline SVG.

The page draws the part B/C flyout language (title line, glyph box row with its command column, separator, a 300 px list, rows that show their command, the outline on the row Enter sends) in three lanes, each over a sketch of the radar around KOAK.

Each lane draws the flyout as it opens with the pointer on N9152M (its target ringed on the scope, with an optional bearing line), the lane's second verb, the filter matching nothing (`dal`), and the empty state (strip button inert, a list emptied while open). Rows, boxes and toggles are live; the line under each lane says what a click or Enter sends.

Two page switches: Solo training (RTIS rows send the descriptive form; Force hidden; also `#solo`) and Bearing line.

- A (recommended): one flyout with a Report in sight `RTIS` / Follow `FOLLOW` toggle; both strip buttons open it preset to their verb. In Follow the reported aircraft comes first, the rest greyed "not in sight"; Force (RPO) makes them `FOLLOWF`.
- B: two flyouts sharing one layout, as Initiate handoff and Point out do.
- C: one Traffic flyout grouped In sight (rows send `FOLLOW`) over Not reported (rows send `RTIS`).

The situation: N52TW, a PA28 VFR inbound to KOAK from the southeast at 2,000 ft, heading 300, having reported N738SP (left downwind 28L) in sight; seven aircraft airborne within 7 NM. Positions follow each row's clock and distance; they are illustrative, not from a recording or scenario.

Found while drawing (static reads, not run): solo training refuses `RTIS {callsign}` and bare `RTIS` (`NavigationCommandHandler.cs:1530`); bare `RTIS` is refused unless traffic is already in sight (:1557); the sim's `FOLLOW` checks only that some traffic is in sight, not the named one (`CommandDispatcher.cs:4090-4103`).

Follow traffic…'s gate reads `LastReportedTrafficCallsign`, which outlives the in-sight flag (`MenuCatalog.cs:445`, `VisualApproachHelper.cs:64`), and it has no All Commands entry; the radar has no menu highlight (`ClientMenuHost.HighlightAircraft` writes ground views only; `RadarCanvas` does not repaint on `HighlightsChanged`); a pending "looking" RTIS is not on the wire. The full code map is `.tmp/map-569-d.md` in the session that drew it.

Flyout sizes, measured from the headless render (open the page with `#measure`): every flyout is 440 px wide (under the 480 px cap). Lane A: 438 tall as it opens (the list scrolls past 300 px), 440 in Follow, 184 on no match, 176 empty. Lane B: 406, 408, 152, 144. Lane C: 408, 408 with Force, 154, 146. Today's rich popup is 430 × 229 and the Follow box 290 × 46.

Not from real data, or not checked:

- The radar is a sketch (runways, a shoreline, a 5 NM ring), not YAAT's radar renderer; the ring and bearing line are proposals.
- The 10 NM radius, the row's direction and leg words (from `HeadingDegrees` and `CurrentPhase`), and the solo descriptive form built from a row are the page's proposals.
- The two left-hand strip buttons are faint stand-ins, not built glyphs.

Source files read for it: `MenuCatalog.cs`, `SharedMenuGroups.cs`, `RelativeTraffic.cs`, `RelativeGeometry.cs`, `MenuTrafficRow.cs`, `IMenuHost.cs`, `IMenuAircraft.cs`, `ClientMenuHost.cs`, `DataBlockViewState.cs`, `RadarCanvas.cs`, `GroundTrafficFlyout.cs`, `CoordinationFlyout.cs`, `FlyoutPanelParts.cs`, `ScopeFeedbackTracker.cs`, `QuickCommandGlyphs.cs`, `QuickCommandDefaults.cs`, `src/Yaat.Sim/Commands/NavigationCommandHandler.cs`, `CommandDispatcher.cs` (`TryAirborneFollow`).

Also COMMANDS.md (FOLLOW, RTIS), and the part C mock `yaat-569-c-coordination/`.
