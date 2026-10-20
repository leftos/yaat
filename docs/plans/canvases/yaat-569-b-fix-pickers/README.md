Mock for YAAT-569 part B (the fix pickers: Direct to…, Append direct to…, Hold at fix left / right…, Cross fix, Depart fix, Join airway with its "(other)" companion, Join radial outbound / inbound, PTAC…, and the strip's Hold at fix… for `HOLDP`): the owner decisions the code map `.tmp/map-569-next.md` (section 2, family B) left open. Open `index.html` in a browser; it works offline and loads no web fonts. It has no image files: every surface is drawn in HTML and inline SVG.

Every flyout draws the built parts: the Fluent dark menu, the title line and glyph rows, the boxes with their command column, the `FlyoutTabStrip` tabs, the heading flyout's toggle and chips, the 480 px cap, and for the radial the heading flyout's `HeadingRose`.

Each decision has lanes A, B (and C) with how it works and its worst case; lane A is the recommended one in every decision. Rows, chips, toggles, tabs, boxes and the rose are live: the line under each lane says what a click, a release or an Enter sends.

The aircraft: UAL455, an A320 from KGEG to KOAK filed `GEG J3 IMB J3 LKV J3 RBL SPAMY WNDSR2`, at FL240, 280 kt, heading 184, 31 NM south of Red Bluff (RBL) and navigating to SPAMY. Decision 7 draws it later, on vectors for runway 30 at 3,000 ft, heading 250.

A note at the top shows the owner's rulings as settled: every quick command gets a designed flyout; single-box inputs become inline boxes; All Commands opens the same flyout from one builder; flyouts live-refill in place every second and rows never move (X5 decision 6, lane A).

Here the distance and bearing columns, the ● next fix and the aircraft's radial follow the aircraft, and a passed fix greys to "passed" in place. Decision 1 has a demo clock that flies UAL455 down the route at 10×: SPAMY greys at about 24 s and the ● moves to HUBRT.

A second note lists what the code forced, found while drawing (static reads, not run):

- Cross fix and Depart fix send half a command. Their route-fix list and single-fix leaf send `CFIX SPAMY` / `DEPART SPAMY` (`BuildOneManyOrInput`; `MenuCatalogCommandTests` asserts `CFIX SUNOL`), but `ApproachCommandParser.ParseCfix` refuses a fix without an altitude and `ParseDepart` wants exactly a fix and a heading. Decision 6 exists for this.
- Join radial sends `JRADO RBL 184` with a space, while `ParseRadialCommand` reads one token whose last three characters are the radial (`RBL184`); with the space the fix it looks up is "RBL " and is not found. The bearing popup also passes a typed `90` unpadded. COMMANDS.md documents the spaced form (`JRADO SJC 150`). The page's flyouts send `JRADO RBL184`.
- `IMenuAircraft` has no position, so the distance and bearing columns need one added (client side; fix positions come from `NavigationDatabase.GetFixPosition`). A Nearby list needs a scan of `GetFixTuples`, and airways through a fix need a fix-to-airway index over `GetAirwayFixes`; neither query exists.

It also recommends, without lanes: the keyboard model (focus in the fix box on open; typing filters and outlines the first match, which Enter sends, else the typed name upper-cased as today; Down into the rows, Up back to the box, Tab through the extra boxes and chips, Escape back to the strip button).

It also recommends FRDs in the fix box (`RBL184031`) as DCT reads them, reusing the strip's Direct to, Hold and Cross fix glyphs (the point menu's Hold left / right racetracks for `HFIXL` / `HFIXR`, Cleared approach's for PTAC), and the ruled screen edge and 480 px cap.

- Today: the filter popup (Direct to over the remaining route; Hold at fix left / right empty until you type), the Cross fix list of route fixes, All Commands › Procedures with the Join airway J3 leaf and the "(other)" entries, the "(other)", bearing, `HOLDP` and PTAC input popups.
- 1. Direct to layout (all lanes: title line naming the next fix, the Fix box on top, focused, a command on every row). A (recommended): one panel, the remaining route as rows (fix, distance and magnetic bearing, command, the next fix filled with ●), then as you type every other matching fix nearest first, the first 50; an FRD gets its own row. B: Route / Nearby / All tabs; typing jumps to All. C: today's flat list inside the flyout, A to Z, no distances.
- 2. One flyout or several for DCT, ADCT, CFIX, DEPART, HOLDP. A (recommended): Direct to and Append direct to share one flyout with a Direct to / Append to route toggle (drawn only while the aircraft navigates to a fix, today's gate); Cross fix, Depart fix and Hold at fix keep their own flyouts built from the same fix list part. B: one flyout with a five-way toggle whose extra boxes follow the verb. C: one flyout per entry, no toggle.
- 3. Hold at fix. A (recommended): the strip's Hold at fix… gets a holding-pattern form under the Fix box: Inbound course (empty reads the route course into the row's fix), Legs chips (1 min, 1½ min, 2 min, 5 NM, 10 NM, opening on 1½ min above 14,000 ft MSL, AIM 5-3-8 Timing), Turns Right / Left (Right first, the parser's default); every route row shows the whole `HOLDP` and sends on a click.

  `HFIXL` / `HFIXR` share a small fix flyout with a Left 360s / Right 360s toggle. B: one Hold flyout with a Racetrack / Left 360s / Right 360s toggle. C: `HOLDP` as one typed box with a parsed command beside it.
- 4. Join airway. A (recommended): the Airway box on top (the "(other)" companion folded in), the filed airways (◆ J3), then every airway through the route's fixes and fixes within 30 NM, nearest first (V25, Q5, V199, V23, V195, J1, J65, J126). B: the filed airways and the box only. C: today's leaf and "(other)" entry kept, the "(other)" a one-box flyout.
- 5. Join radial. A (recommended): one flyout with an Outbound `JRADO` / Inbound `JRADI` toggle, the Fix box, the navaids nearest first (each naming the radial and distance UAL455 is on now), a Radial box, and `HeadingRose` reused with the fix in its centre and the needle on the aircraft's radial; it opens with the nearest route navaid picked.

  Drawn twice: Outbound with nothing picked, Inbound with the 150 radial picked. B: the same without the rose. C: two steps inside the flyout, as today's two popups.
- 6. Cross fix and Depart fix, the second value. A (recommended): boxes under the Fix box, then the list: for Cross fix a Cross at toggle (At / At or above `A` / At or below `B`, opening on At or above), an Altitude box and an optional Speed box (`CFIX SPAMY A110 250`); for Depart fix a Heading box (`DEPART SPAMY 200`).

  A click on a row waits for the second value: it picks the fix and focuses the box. B: the whole restriction typed after the fix (`SPAMY A110 250`). C: the altitude taken from a Maintain altitude list under the fix list.
- 7. PTAC (added: the last one-box popup in Procedures, which the code forces into this part). A (recommended): a PTAC / Forced toggle, Heading and Altitude boxes (empty sends `PH` / `PA`), then approach rows (the expected approach first, ●) each sending its whole command. B: one typed box with the command parsed beside it.

Data. Fix positions, airways and names come from the repo's `tests/Yaat.Sim.Tests/TestData/FAACIFP18.gz`: the route and STAR fixes (WNDSR2 common route and runway 30 transition: SPAMY, HUBRT, WNDSR, WEBRR, BOYYS, HOPTA, ALLXX, CRSEN), J3 (OAK, RBL, LKV, IMB, GEG) and the airways through RBL and the nearby fixes.

The page embeds the 1,054 CIFP fixes, VORs and KOAK terminal fixes within 150 NM of the aircraft and computes distances and bearings from them in its script; the app searches every fix. Prefix counts quoted from the CIFP (enroute waypoints and VORs): about 235 names start with SA, and SAC is the 26th A to Z.

The route lists follow the code: the Direct to rows are `DirectToFixNames` (the navigation route from the fix navigated to, the filed route after it, the destination) and the Cross fix list is `RouteFixNames` (the navigation route, the expanded filed route, the destination, the departure). Both are derived by reading the code, not by running it.

Flyout sizes quoted on the page and here were measured from the headless render (open the page with `#measure` to list every frame's size). Decision 1: 340 × 394 px (A), 340 × 380 (B), 340 × 394 (C). Decision 2: A 340 × 339; B 450 px wide and 309 px tall on Direct, 370 on Depart, 437 on Hold, 467 on Cross; C 340 × 307. Decision 3: A 384 × 405 (the 360s flyout 340 × 309), B 384 × 437, C 345 × 277.

Decision 4: A 393 × 367, B 351 × 156, C's one-box flyout 340 × 98. Decision 5: A 340 × 613, the tallest flyout on the page; B 340 × 380; C 340 × 307. Decision 6: A's Cross fix 342 × 455 and Depart fix 340 × 358, B 340 × 327, C 342 × 474. Decision 7: A 358 × 354, B 404 × 128. No flyout reaches the 480 px cap. Today's filter popup is 210 × 292, the route-fix list 122 × 302, the input popups 280 × 46 (the bearing one 310).

Not from real data, or not checked:

- The callsign, the A320 and the aircraft state (FL240, 280 kt, heading 184, the position 31 NM south of RBL) are illustrative; the filed route is a real KGEG–KOAK route from the scenario example `docs/atctrainer-scenario-examples/01J02M96SPYP4JV55R5RMVCQBS.json`, which spawns its aircraft at BOYYS instead.
- That the navigation route carries the WNDSR2 runway 30 transition (ALLXX, CRSEN) assumes runway 30 is assigned.
- Bearings use a fixed 13° east magnetic variation, not the sim's `MagneticDeclination`; the radials ("on its 184 radial") are the same approximation.
- The demo's ground speed (380 kt) and the 10× clock are illustrative.
- The KOAK approach rows in decision 7 (ILS30, RNAV30, ILS28R, ILS28L) and the expected approach (ILS30) are illustrative IDs, not read from `ApproachChoices`; decision 7's vectoring state is illustrative.
- The route-course default for the inbound course and the 1½ min leg default are design proposals that need the aviation-sim-expert review before a brief.
- The box parsers are the page's approximations: altitudes read hundreds for up to three digits and feet above (as `AltitudeResolver` and `ParseCfixAltitudeToken` do), the Airway box accepts J, V, Q or T and digits where the app would ask `IsAirway`, and "Not a known fix" is judged against the page's 150 NM set.
- Append direct to, Depart fix, Join airway and Join radial have no glyph; the page draws dashed drafts in the flight blue.

Source files read for it: `.tmp/map-569-next.md`, `src/Yaat.Client.Core/ContextMenus/MenuCatalog.cs` (`FixPicker`, `BuildFixPicker`, `DirectToFixes`, `RouteFixes`, `NoRouteFixes`, `IsNavigatingToFix`, `RouteFixPicker`, `BuildCrossFixOther`, `BuildDepartFixOther`, `BuildJoinAirway`, `FiledAirways`, `BuildOneManyOrInput`, `BuildOtherInput`, `RadialPicker`, `BuildRadialPicker`, the PTAC… and Hold at fix… entries, `BuildList`, `BuildFilteredList`).

Also from the menu code: `SharedMenuGroups.cs` (Navigation, Hold, Procedures), `IMenuHost.cs` (`FixNames`, `SuggestFixes`) and `IMenuAircraft.cs`.

Also read: `src/Yaat.Client/Views/MenuPopups.cs` (`ShowInput`, `ShowList`, `ShowFilteredList`, `FilterNames`), `HeadingFlyout.cs` (the panel and `HeadingRose`), `FlyoutPanelParts.cs`, `TakeoffFlyout.cs` (the fix box), `QuickCommandGlyphs.cs`, `QuickCommandDefaults.cs`, `FixSuggester.CollectRouteFixNames`.

From the sim and the references: `src/Yaat.Sim/Commands/ApproachCommandParser.cs` (`ParseHold`, `ParsePtac`, `ParseCfix`, `ParseDepart`, `ParseRadialCommand`, `ParseCfixAltitudeToken`), `CommandRegistry.cs` (JRADO / JRADI, HOLDP, PTAC), `NavigationDatabase.cs`, COMMANDS.md (Navigation, Hold, Approach / Procedures, Holding Patterns, Direct To), AIM 5-3-8, and the mock `yaat-569-x5-flight-pickers/`.
