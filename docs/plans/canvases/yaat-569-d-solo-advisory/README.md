Mock for YAAT-569 part D (traffic flyouts), solo training: how a student gives the spoken-form traffic advisory (`RTIS <clock> <miles> <direction> <type> [altitude]` and its nose, pattern-leg and landmark forms) and the field advisory (`RFIS <clock> <miles>`) from the Report traffic in sight… and Report field in sight… flyouts, since solo training refuses `RTIS {callsign}`, bare `RTIS` and bare `RFIS` (`src/Yaat.Sim/Commands/NavigationCommandHandler.cs` :1571-1574 for RFIS, :1657-1660 for RTIS).

It builds on the owner's rulings in YAAT-569: lane B of the part D mock (`../yaat-569-d-traffic/`, two flyouts sharing one layout), greyed Follow rows with an RPO-only Force, and the radar cue (a ring on the target and a dashed bearing line from the selected aircraft). Open `index.html` in a browser; it works offline, loads no web fonts and has no image files: every surface, the radar excerpt included, is drawn in HTML and inline SVG.

Three lanes, each drawn over a sketch of the radar around KOAK with N52TW (PA28, VFR inbound, 2,000 ft, heading 300) as the student's aircraft:

- A, chip line: a click on a row fills an editable advisory under the box, one chip per part, stepped with ‹ › or the arrow keys; form tabs Radar · Nose · Pattern · Over; Enter sends.
- B, builder: a clock dial with the nose at 12 (the radar draws the same numbers round the aircraft), a miles stepper, an 8-point direction pad, type and altitude, seeded from the pointed row until one is clicked; the pattern form is a pattern sketch whose legs are clicked.
- C, say it: the box takes the short form as the student would say it; Tab takes the next part from the pointed row, and a live check names the aircraft the sim would resolve the call to and grades each part.

Each lane shows the flyout open with an aircraft picked (ring and bearing line), the filled advisory and what Enter sends, the pattern-leg form near the field, the field advisory, a refused call, a call that resolves to other traffic, a form the aircraft cannot take (lane C: no such traffic), an incomplete call and the empty state. A page switch, Pre-filled values, shows every lane with the true values or with clock, miles and direction left for the student (also `#blank`); the open question is which one teaches.

The answers drawn ("resolves N22RX, scored clean", the refusal texts) imitate `TrafficAdvisoryMatcher` (tolerances :68-83, refusals :151, :262, :346, :411) on the drawn positions, which are illustrative, not from a recording. Spoken text follows `CommandDescriber.FormatTraffic*Phrase` (:2750-2793); the aircraft type words are illustrative.

Found while drawing (static reads, not run): the sim does not check `RFIS` clock or miles (`NavigationCommandHandler.cs:1649-1653`, `SoloTrainingEvaluator.cs:142-145`); the matcher's refusal reads "Wbound" (`TrafficAdvisoryMatcher.Describe`, :481-485); no dry-run of the matcher exists for a check before sending.

Flyout sizes from the headless render (open with `#measure`), all 440 px wide: lane A 561 tall as drawn in the scene (the list scrolls past 300 px), 245 for the field advisory; lane B 686 and 343; lane C 494 and 220.
