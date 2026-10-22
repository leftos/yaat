Mock for YAAT-1018 family T (text boxes): the three free-text input popups left once families F and E are converted, Squawk… (`SQ {code}`, `MenuCatalog.cs:93-100`, the only checked one), Ask pilot to say › Custom… (`SAY {text}`, `:113-119`) and Data Block › Scratchpad… (`SP {text}`, `:137`). Converting them leaves Custom taxi… (`MenuCatalog.cs:1192-1210`) as the one caller of `IMenuHost.ShowInputPopup`.

Today's behaviour is `MenuCatalog.BuildInput` / `BuildCheckedInput` (`MenuCatalog.cs:919-966`), the Squawk, Ask pilot and Data Block groups (`SharedMenuGroups.cs:49-98`) and `MenuPopups.ShowInput` (`src/Yaat.Client/Views/MenuPopups.cs:48-159`).

Open `index.html` in a browser; it works offline and loads no web fonts. It has no image files: every surface is drawn in HTML and inline SVG.

## How to read the page

The page opens with what earlier rulings settled and what was found in the source while drawing. Then comes "Today", the surfaces as built, followed by one artboard per decision, each option a lane with a line saying what a click or Enter sends, how it works and its worst case in use. Rows, chips and boxes are live.

- 1 Box form: the Squawk flyout (N123AB, C172, VFR flight following, squawking 1200, 4613 assigned) with the code box as an inline always-open row, or as a row that opens its box in place.
- 2 Say › Custom: the Say flyout (UAL123, B738) with recent customs, a few presets, or the box alone.
- 3 Scratchpad: the one-box flyout (SWA2471, B737, scratchpad I30) seeded with the present value, or empty.
- 4 Squawk code check: `1289` typed, refused inline with a reason line, or the send disabled.
- 5 ShowInputPopup: the Custom taxi… popup with the host interface kept as is, or shrunk.

Three page switches answer decisions 6, 7 and 9: the scratchpad length check, what a blank Scratchpad sends and what a blank Squawk sends.

## The decisions, numbered as on the page

1. Box form: a inline box row, always open, as CTO Custom; b a row that opens its box in place.
2. Say › Custom: a recent customs; b a few presets; c nothing.
3. Scratchpad seed: a the present value, selected; b empty, the present value in the title.
4. Squawk code check: a refuse inline with a reason line; b disable the send until valid.
5. ShowInputPopup: a keep the refusal line and both `BlankInput` modes; b shrink to seeded text, caret and close, deleting `BlankInput`.
6. Scratchpad length (the limit is 3, or 4 where the facility allows, `ScratchpadRuleEngine.cs:16`, and the client never receives it): a check at 4; b carry the room's limit to the client; c no check.
7. Blank Scratchpad (`SP` alone clears, `CommandParser.cs:1053`): a sends nothing; b sends `SP`.
8. Blank Say › Custom (forced: a bare `SAY` parses as an unsupported command, `CommandParser.cs:1078`, `:1138`): sends nothing.
9. Blank Squawk (`SQ` alone is Squawk assigned code, `CommandParser.cs:4040-4043`): a sends nothing; b sends `SQ`.

Each option's worst case is on the page.

## Not from real data, or not checked

- Callsigns, codes, scratchpads and the recent Say texts are illustrative.
- The recent-customs list, the preset chips, the inline boxes, the reason lines, the amber toggle note and the shrunk `ShowInputPopup` signature are drawings of options, not built UI.
- The findings are static reads of the source, not run in the client or the sim: the squawk box being stricter than the parser, the SP toggle and clear-undo (`TrackEngine.cs:403-432`), the bare SAY refusal, the bare SQ result (`FlightCommandHandler.cs:600-606`).

Client source read for it: `MenuCatalog.cs`, `SharedMenuGroups.cs`, `AltitudeFlyout.cs` (`AltitudeBoxPanel`), `IMenuHost.cs`, `BlankInput.cs`, `IMenuAircraft.cs`, `QuickCommandGlyphs.cs`, `src/Yaat.Client/Views/MenuPopups.cs`, `ClientMenuHost.cs` (`GetCustomTaxiSeed`).

Sim source and docs read for it: `src/Yaat.Sim/Commands/CommandParser.cs` (`ParseSquawkOrReset`, the SAY and SP arms), `CommandDispatcher.cs`, `TrackEngine.cs` (`HandleScratchpad1`), `ScratchpadRuleEngine.cs`, `FlightCommandHandler.cs` (`ApplySquawkReset`), COMMANDS.md (scratchpads, Say), and the family F and E mocks `yaat-569-f-pickers/` and `yaat-569-e-report-when/`.
