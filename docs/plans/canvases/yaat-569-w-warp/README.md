Mock for YAAT-1018 family W (Warp): Warp… at the end of All Commands (`MenuCatalog.BuildWarp`, `MenuCatalog.cs:1078-1088`) and Warp here on the point menu at a map point (`BuildPointWarp`, `:1216-1231`), which both open `IMenuHost.ShowWarpPopup` and send `WARP {frd} {heading} {altitude} {speed}` (`:1095-1113`). At a taxi node Warp here sends `WARPG #id` at once and stays a row.

Today's popup is `MenuPopups.ShowWarp` (`src/Yaat.Client/Views/MenuPopups.cs:257-344`); the sim side is `CommandParser.ParseWarp` (`src/Yaat.Sim/Commands/CommandParser.cs:2521-2611`) and `FlightCommandHandler.ApplyWarp` (`src/Yaat.Sim/Commands/FlightCommandHandler.cs:999-1038`).

Open `index.html` in a browser; it works offline and loads no web fonts. It has no image files: every surface is drawn in HTML and inline SVG.

## Measured

- WARP's heading is magnetic: parsed as a `MagneticHeading` (`CommandParser.cs:2610`), turned true with the aircraft's cached declination (`FlightCommandHandler.cs:1018-1019`, `:1023`), kept as the assigned magnetic heading (`:1024`). The declination is the one cached for the pre-warp position (`FlightPhysics.cs:107-138`); what the heading hold does after a long warp is unmeasured.
- Heading, altitude and speed are optional: an omitted one keeps the present value (`FlightCommandHandler.cs:1001-1003`), slot-filled left to right (`CommandParser.cs:2570-2607`), as `COMMANDS.md:2347-2360` documents.
- An altitude under 1000 is read as hundreds (`AltitudeResolver.cs:19-26`), so today's "Altitude (feet)" box sends 800 ft as 80,000 ft.

## How to read the page

The page opens with the measured facts and what was found while drawing, then "Today", then one artboard per decision, each option a lane with a line saying what Enter sends, how it works and its worst case in use. Boxes, chips and fix suggestions are live; each field's command column shows what it puts in the command, and the last row the whole command.

- 1 Fields (UAL123, B738, 11,000 ft): all four mandatory and seeded, or the trailing three optional.
- 2 Units and presets (N123AB, C172, KPAO pattern at 800 ft): altitude in feet, altitude as the menu's altitude boxes read it, heading and speed chips.
- 3 The FRD field (UAL123): typed with suggestions, or seeded with the aircraft's own FRD; Warp here below each.
- 4 A field that does not read (SWA2471 parked at KOAK): inline per-field refusal, or a disabled send.

## The decisions, numbered as on the page

1. Fields: a all four mandatory, seeded; b the trailing three optional, a blank keeping the present value.
2. Units and presets: a altitude in feet; b altitude as the altitude boxes read it; c heading and speed chips.
3. The FRD field: a free text with fix suggestions; b seeded with the aircraft's own FRD.
4. A field that does not read: a inline per-field refusal; b disabled send.
5. A blank before a filled field (forced under 1b by the slot filling): the present value goes in its place.
6. Warp… on the ground (WARP always leaves the aircraft airborne, `FlightCommandHandler.cs:1035`): a offered, saying so; b hidden, WARPG at a taxi node being the menu's ground warp.

Each option's worst case is on the page.

## Not from real data, or not checked

- Callsigns, positions, FRDs and the KPAO pattern are illustrative; the mock knows a handful of Bay Area fixes and its "Not a known fix" stands for the nav-data check.
- The flyout, its per-field command column, the chips, the suggestions, the amber "kept" values and every red line are drawings of options, not built UI. The "to fix" heading chip is the FRD radial's reciprocal, not a computed course.
- The findings are static reads of the source, not run in the client or the sim.

Source files read for it: `MenuCatalog.cs`, `IMenuHost.cs`, `QuickCommandGlyphs.cs`, `AircraftCommandApplicability.cs`, `src/Yaat.Client/Views/MenuPopups.cs`, `ClientMenuHost.cs`, `RadarViewModel.cs` (`TryToFrd`), `src/Yaat.Sim/Commands/CommandParser.cs` (`ParseWarp`), `FlightCommandHandler.cs` (`ApplyWarp`), `AltitudeResolver.cs`, `FlightPhysics.cs` (`RefreshDeclinationCache`), `AircraftState.cs`, `FrdResolver.cs`, COMMANDS.md (WARP, FRD), and the family F and E mocks.
