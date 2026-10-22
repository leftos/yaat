Mock for YAAT-1018 family E (Report when…): the All Commands › Approach › Report when… group, which the owner's "siblings are absorbed" ruling turns into one flyout carrying the turn reports (`REPORT CROSSWIND`, `DOWNWIND`, `BASE`, `FINAL`), At N-mile final… (`REPORT {n} FINAL`), At fix… (`REPORT {fix}`) and Stop reporting (`REPORT OFF {leg}`, `REPORT OFF`). Converting it retires two of `IMenuHost.ShowInputPopup`'s callers (`MenuCatalog.cs:180-181`, the `InputLeaf` entries over `BuildInput`).

Today's behaviour is `src/Yaat.Client.Core/ContextMenus/SharedMenuGroups.cs:576-598` (the submenus) and `MenuCatalog.cs:176-186` (the entries).

Open `index.html` in a browser; it works offline and loads no web fonts. It has no image files: every surface is drawn in HTML and inline SVG.

## How to read the page

The page opens with what earlier rulings settled and what was found in the source while drawing. Then comes "Today", the surfaces as built, followed by two artboards, each with three lanes side by side, each lane with a line saying what a click or Enter sends, how it works and its worst case in use. Rows, chips, sketch legs, tabs, the verb toggle and the boxes are live.

Three page switches: Check the boxes (decision 3), Grey what the sim refuses (decision 5) and Armed reports reach the client (decision 7).

- 1 VFR arrival inbound, no runway yet (N52TW, PA28, 9 NM south-east of KOAK, heading 310), opened from the strip's At N-mile final… button.
- 2 In the pattern (N738SP, C172, closed traffic, right downwind 28R, 1.4 NM from the threshold), opened from All Commands.

The lanes:

- A: rows, preset chips (2, 3, 5, 10) and checked boxes; reporting points nearest first; Stop reporting as the bottom section.
- B: a pattern sketch whose legs are clicked, a final ladder of 10 to 1 NM with the aircraft's distance marked, the reporting points; Stop reporting on a tab.
- C: one box shaped like the parser (`base`, `5 final`, a bare `5`, `VPCOL`) with suggestion rows under it; Stop reporting as a Report / Stop verb toggle.

## The decisions, numbered as on the page

1. Lane: A, B or C.
2. Mile final input: a chips and a box, b a ladder, c three suggestion rows under one box.
3. Checking the boxes: a the flyout reads a box as the sim will and refuses in red, b send as typed.
4. Where the fixes come from: a reporting points near the aircraft then a typed search, b the route's fixes then a typed search, c a box only.
5. Greying what the sim refuses: a grey turn rows without a pattern leg in the phase pipeline and Mile final without a runway, b every row live.
6. Where Stop reporting sits: a the bottom section, b a tab, c a verb toggle.
7. Armed reports: a none, as today, b the aircraft update carries them (a field in both repos).
8. The two strip buttons: a both stay, each opening the flyout on its own row, b one Report when… button replaces both.

Each option's worst case is on the page.

## Not from real data, or not checked

- Callsigns, aircraft positions, headings and states are illustrative. Reporting-point distances and bearings come from approximate coordinates and a 13° east declination, not from the nav data.
- The reporting points and their names are the bundled ZOA visual points (`src/Yaat.Sim/Data/ARTCCs/ZOA/FixPronunciations/visual.json`) and `OAK30NUM` (`CustomFixes/oak-landmarks.json`).
- The mock knows a handful of fixes; its "Not a known fix" stands for the nav-data check.
- The sketch, the ladder, the chips, the verb toggle, the "armed" marks, the inside-now notes and every red line are drawings of options, not built UI. Lane C reading a bare number as miles is an option the parser does not take today (`REPORT 5` is refused).
- The findings on the page are static reads of the source, not run in the client or the sim: the straight-line mile-final trigger and its inbound-to-land gate (`PilotProactive.cs:198-206`, `ApproachCommandHandler.cs:2605-2614`), the 0.5 NM fix trigger, the cancel replies, the 2-5 character fix rule.

Source files read for it: `MenuCatalog.cs`, `SharedMenuGroups.cs`, `QuickCommandDefaults.cs`, `QuickCommandGlyphs.cs`, `IMenuAircraft.cs`, `HoldShortLadder.cs`, `src/Yaat.Client/Views/MenuPopups.cs`, `src/Yaat.Sim/Commands/CommandParser.cs` (`ParseReport`), `NavigationCommandHandler.cs` (`DispatchReport`), `AircraftApproachState.cs`, `Pilot/PilotProactive.cs` (`TickReportTriggers`), `ApproachCommandHandler.cs` (`IsInboundToLand`), `docs/client-context-menus.md`, COMMANDS.md (`REPORT`), and the family F mock `yaat-569-f-pickers/`.
