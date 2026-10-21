Mock for YAAT-569 part D, solo training: how the live check under the say-it box behaves, and where the field advisory lives. It builds on the owner's choice of lane C in the solo advisory mock (`../yaat-569-d-solo-advisory/`): the student types the short form (`2 4 w sr22`) in the Report traffic in sight flyout, the lane B builder sits below it in solo, and a live check names the aircraft the sim would resolve the call to and grades each part.

The check runs on the client against the client's aircraft list and is its own solo setting, default Off; every lane draws it On.

Open `index.html` in a browser; it works offline, loads no web fonts and has no image files: the scope excerpt, flyouts and menus are HTML and inline SVG. Sections 1 and 2 are animated, with Replay, Play/Pause, a time bar, a clock and a speed. `#t=1200` freezes both at 1.2 s, `#t=1200&t2=9000` sets section 2 separately, and `#turn=240` picks section 2's longer turn.

The situation is the earlier mock's: N52TW, a PA28 VFR inbound to KOAK at 2,000 ft heading 300; N22RX (SR22, westbound, 3,500) at 2 o'clock, 4 miles. Each lane draws the scope excerpt above the flyout, not under it, so lanes fit side by side. The flyout's traffic rows show the true clock and miles, the earlier mock's default for its open Pre-filled values question.

## 1. Check cadence (animated)

All four lanes type `2 4 w sr22` at the same moments (about 140 ms between letters, half a second between parts) and press Enter at 4.3 s; a bar under each lane marks keystrokes and check runs, and a line under it reads what the check line showed at Enter.

- A, every keystroke: the line re-grades on each key, so `s` and `sr` grade as a type mismatch (imprecise) until `sr2` matches on three letters.
- B, debounced 300 ms: the line re-grades once the keys rest for 300 ms; four checks in the sequence.
- C, part boundary: the line re-grades on Space or Tab; the last part has no boundary before Enter, so at Enter it still reads `needs ‹type›`.
- D, preview key: nothing is graded until the preview key (drawn as `Ctrl+Space`, a placeholder) at 3.5 s.

A page switch sets what B to D show while the text is past the last check: the last grade dimmed, nothing, or "checking…".

Open question: which cadence, and for B to D, which between-checks display.

## 2. Snapshot drift while typing (animated)

The student types `2 4 w sr22`, then pauses while N52TW turns left at standard rate (3°/s) from 300 to 270, so N22RX drifts from 2 to 3 o'clock; the client sees each step at its once-a-second update, and Enter comes one second after the turn ends. Both lanes check on every keystroke while typing. A switch continues the turn to 240 (N22RX to 4 o'clock), since a one-hour drift stays inside the matcher's clean band and only the clock mark's detail changes.

- A, re-grade when the aircraft moves: the check re-runs on each aircraft-list update as well as on each key; the line follows the drift and flashes when its text changes.
- B, re-grade only when the text changes: the grade holds; once a part it graded reads differently on the client it dims and gets a stale mark. Stale compares the graded parts rather than every update, since traffic moves every second.

Open question: re-grade on motion, or hold the grade and mark it stale.

## 3. Which aircraft stays ringed (static)

The student pointed at N811CP (4 o'clock, 7 miles, southbound) and typed `5 6 nw c182 020`, which the direction gate drops for N811CP and resolves to N4417Q (6 o'clock, 5 miles, northwestbound), clean.

- A: the pointed row keeps the solid ring and bearing line; the resolved aircraft gets a second, dashed ring.
- B: the ring and bearing line move to the resolved aircraft (the earlier mock's lane C drawing).
- C: the ring stays on the pointed row; the check line names N4417Q in the warning colour and says it is not the row pointed at.

Open question: which ring rule; the warning text of C can also go with A or B.

## 4. Field advisory placement (static)

`RFIS <clock> <miles>` takes the same say-it box with no live check; a faint "not checked" sits where the check line would be in every lane.

- A: one flyout with a Traffic · Field toggle under the title, opened from one strip well.
- B: its own strip well and small flyout, as the earlier mock drew it.
- C: a Field section with its own box below the traffic list, in the same flyout.

Open question: which placement.

## Sources imitated

- Drawing: `../yaat-569-d-solo-advisory/index.html` (page and app-surface CSS :9-263, glyph paths :333-338, lane C box and check line :534-536 and :659-669, scope excerpt :749-767, aircraft menu stub :737-745).
- The check: `src/Yaat.Sim/Commands/TrafficAdvisoryMatcher.cs` (gates and clean bands :65-75, weighted error :134-135, Grade's "clock off N hr" / "distance off N NM" wording :448-470, three-letter type match :514).
- Spoken lines: `src/Yaat.Sim/Commands/CommandDescriber.cs` (`FormatTrafficAdvisoryPhrase` :2750, `FormatFieldAdvisoryPhrase` :2865).
- The client's aircraft list: `src/Yaat.Client/ViewModels/MainViewModel.cs:999` (`Aircraft`), fed by `MainViewModel.Aircraft.cs:209` (`OnAircraftUpdated`); the server broadcasts once per sim-second (`docs/tick-loop.md:185`).
- The field advisory is not graded: `src/Yaat.Sim/Commands/NavigationCommandHandler.cs:1649-1653`, `src/Yaat.Sim/Training/SoloTrainingEvaluator.cs:142`.

Positions and type words are illustrative, not from a recording; the turn's timing is computed at standard rate, and the matcher's wider clock gate for a manoeuvring aircraft is not drawn.
