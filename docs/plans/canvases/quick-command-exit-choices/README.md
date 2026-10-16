Mock for YAAT-569 (designed quick-command flyouts), brief X1: the four open choices left on the Exit left / Exit right flyouts. They are drawn on the settled first-batch layouts (Exit left on the rollout as lane A, Exit right on final as lane B) with real KOAK 28R and KSFO 28L exits from the bundled layouts. Open `quick-command-exit-choices.html` in a browser.

Each question has two lanes, A and B, with how it works and its worst case under each:

1. Exit glyph angle: A, one fixed glyph per side; B, each glyph drawn at the exit's real turn-off angle, which needs a new angle field on `ExitAheadDto`.
2. The "Exit at" box on Exit right: A, free text sent as typed; B, suggestions from the runway's exits, Enter taking the top match.
3. Live refill while open, drawn at open and 5 s later: A, built once and frozen; B, the ground family's live rule, a passed exit greyed and disabled.
4. Long text against the 480 px cap: A, truncate the note with the full text in a tooltip; B, wrap the row onto a second line.
