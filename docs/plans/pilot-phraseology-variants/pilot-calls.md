# Pilot-initiated call variants (YAAT-1082)

What this is: the catalogue of spoken variants, by load band, for the calls a solo pilot starts itself (check-ins, ready calls, reports, requests), the companion of `readbacks.md`.
Status: reviewed by the owner (2026-10-23); rulings applied.
How to mark it up: strike a variant (`~~…~~`), add one as a new row with your ruling, or move a row to another band; overrule a ruling by editing its cell.

## Scope and notation

Settled frame (from [pilot-phraseology-variation.md](../pilot-phraseology-variation.md)): three bands (quiet, normal, busy); the pilot's trait shifts the band one step; a seeded pick chooses among the band's rows marked OK. Only the spoken form varies; the Terminal stays canonical. Every row also obeys the aviation floor F1 to F14 of that file.

Notation: `{facility}` the addressed position as spoken today, `{callsign}` the spoken callsign (part E, YAAT-1080, decides its form), `{atis}` the ATIS code word, `{freq}` a frequency (part F). Runways, altitudes, taxiways and fixes are written as the TTS speaks them. "Canonical" is today's spoken text, quoted from `src/Yaat.Sim/Pilot/PilotResponder.cs` or `docs/pilot-phraseology-examples.md`.

Ruling column: **OK**; **OK (J)** where the rule is my judgement and no FAA text decides it; **No: Fn / Pn** naming the rule a rejected candidate breaks. Rejected rows stay where they teach the boundary.

Unable calls are out: every call that says "unable" (`BuildUnable*`, `BuildUnableVisualRequestVectors`, `BuildUnableToInterceptRequestVectors`, `BuildUnableToFollowExtendingDownwind`, `BuildFollowExtendingUnableToTurn`) keeps its fixed wording in every band, so a refusal stays unmistakable (owner ruling).

## Floor for pilot-initiated calls (P1 to P9)

These add to F1 to F14 for calls the pilot starts. A variant that breaks one falls back to canonical. The owner approved P1 to P9 and the families held canonical-only as drafted, except where these rulings change them:

1. Busy may drop the word "runway" (number and L/R/C always stay): P8 below.
2. Busy may say altitude thousands below FL180 as a group ("eleven thousand"); quiet and normal keep digits: P7 below, and F3 as restated in `readbacks.md` ("Floor as the owner's rulings change it").
3. An initial check-in below FL180 may say "climbing to" / "descending to" as AIM 5-3-1.b.2(a) prints it: P6 below. Readbacks keep the earlier ruling (no "to" before an altitude below FL180).

"Owner ruling 1, 2, 3" in a Ruling cell names these.

| # | Rule | Cite |
|---|---|---|
| P1 | An initial contact (first call to this controller) keeps the facility name, `{facility}` as the canonical has it. A later call to the same controller may drop it. | AIM 4-2-3.a.1(a), 4-2-3.c |
| P2 | An initial contact keeps the full callsign; part E may shorten it only on later calls to the same controller. | AIM 4-2-4.a.1, 4-2-3.a.1(b) |
| P3 | A call from the airport surface keeps its position (stand, spot, taxiway, hold-short point, runway, airport). | AIM 4-2-3.a.1(c), 4-3-18.a.1, 4-3-10.e |
| P4 | The callsign stays where the canonical puts it (after the facility, or first); it never moves to the end of a pilot-initiated call. | AIM 4-2-3.a.1 order; 4-2-3.c "same format" (J: the start-or-end freedom of 4-2-3.c is read as applying to acknowledgements) |
| P5 | The message type stays: "ready to taxi", "ready for departure", "request …", "inbound", "passing", "lost sight of", the approach type, the IFR/VFR intent and destination. | AIM 4-2-3.a.1(d); 4-1-18.a.3, a.5.b |
| P6 | IFR radar check-in: "level", "climbing" or "climbing to", "descending" or "descending to" before the altitude, in every band and for altitudes and flight levels alike (the "to" forms are AIM's own words, owner ruling 3), and "leaving {alt}" when vacating; climb via / descend via keep the procedure name and "for {alt}" when ATC assigned one, never "to". | AIM 5-3-1.b.2(a), 5-2-9.h.9, 5-4-1.a.2(b); owner ruling 3 |
| P7 | Every distance keeps "miles" / "mile final"; every altitude follows F3 as restated in `readbacks.md`: quiet and normal in separate digits, busy may use group form below FL180 ("leaving twelve thousand"), flight levels always separate digits. | AIM 4-2-9.a; 7110.65 2-4-17.b.1 NOTE, 2-4-17.b.2; F3 by analogy (J); owner ruling 2 |
| P8 | Quiet and normal never drop the word "runway" where the canonical says it. Busy may drop it; the runway number and L/R/C always stay, and a call never loses the runway altogether. A hold-short report still keeps "runway" in every band (F6). | F5; 7110.65 2-4-3 NOTE 1.c; owner ruling 1 |
| P9 | The ATIS code word on an initial contact stays when the canonical has it, after "information", "with" or "have"; "have numbers" never stands in for it. A variant adds nothing the canonical lacks (no invented intent, side or request). | AIM 4-1-13.d, 4-1-13.h, 4-2-3.a.3; F13 by analogy |

Courtesy in pilot-initiated calls (all **J**; no FAA text names these words):

| Word | Quiet | Normal | Busy | Note |
|---|---|---|---|---|
| "good morning / afternoon / evening" after `{facility}` on an initial contact | OK (J) | No | No | Needs the field's local time; drop the row if the sim has none. Busy: AIM 4-2-1.b brevity. |
| "we're …" / "we have …" | OK (J) | No | No | Only where every element survives. |
| "with you" before the altitude | OK (J) | No | No | Common, adds nothing; AIM 4-2-1.b. |
| "alright, " | No | No | No | Nothing to acknowledge on a call the pilot starts. |
| ", thanks" | No | No | No | Nothing has been given yet; the owner may want it on quiet requests. |
| "have numbers" | No | No | No | P9, AIM 4-1-13.h. |

## 1. Initial check-ins

### 1a. IFR airborne check-in (`BuildIfrAirborne`)

| Case | Canonical (spoken) |
|---|---|
| Level, approach | `{facility}, {callsign}, level one zero thousand, with information {atis}.` |
| Climbing (departure, no ATIS) | `{facility}, {callsign}, leaving four thousand climbing one one thousand.` |
| Descending | `{facility}, {callsign}, leaving one two thousand descending eight thousand, with information {atis}.` |
| Center, flight level | `{facility}, {callsign}, flight level two four zero.` |
| Climb via | `{facility}, {callsign}, leaving two thousand for one zero thousand, climbing via the {sid} departure.` |
| Descend via | `{facility}, {callsign}, leaving flight level two four zero, descending via the {star} arrival, with information {atis}.` |
| Tower (rare) | `{facility}, {callsign}, runway two eight right, with information {atis}.` |

| Band | Variant | Ruling |
|---|---|---|
| Quiet | `{facility}, {callsign}, level one zero thousand, we have information {atis}.` | OK (4-1-13.d) |
| Quiet | `{facility}, good afternoon, {callsign}, level one zero thousand, with information {atis}.` | OK (J), courtesy table |
| Quiet | `{facility}, {callsign}, with you level one zero thousand, with information {atis}.` | OK (J), courtesy table |
| Normal | canonical | OK |
| Normal | `{facility}, {callsign}, level one zero thousand, information {atis}.` | OK (4-2-3.a.3 "Information Charlie received") |
| Normal | `{facility}, {callsign}, passing four thousand climbing one one thousand.` | OK (J): "passing" for AIM's "leaving", both altitudes and the verb kept |
| Quiet, Normal, Busy | `{facility}, {callsign}, leaving four thousand climbing to one one thousand.` | OK (owner ruling 3; AIM 5-3-1.b.2(a) example 2, verbatim) |
| Quiet, Normal, Busy | `{facility}, {callsign}, leaving one two thousand descending to eight thousand, with information {atis}.` | OK (owner ruling 3) |
| Busy | `{facility}, {callsign}, level ten thousand, with {atis}.` | OK (owner ruling 2, group form) |
| Busy | `{facility}, {callsign}, leaving four thousand climbing to eleven thousand.` | OK (owner rulings 2 and 3) |
| Busy | `{facility}, {callsign}, leaving twelve thousand descending eight thousand, with {atis}.` | OK (owner ruling 2) |
| Busy | `{facility}, {callsign}, leaving two thousand for ten thousand, climbing via the {sid}.` | OK (owner ruling 2; "for" kept, P6) |
| Busy | `{facility}, {callsign}, level one zero thousand, with {atis}.` | OK (owner example "with India"; 4-1-13.d) |
| Busy | `{facility}, {callsign}, leaving two thousand for one zero thousand, climbing via the {sid}.` | OK (J): "departure" / "arrival" dropped, procedure name kept |
| Busy | `{facility}, {callsign}, leaving one six thousand climbing to flight level two three zero.` | OK (5-3-1.b.2(a); owner ruling 3) |
| Busy | `{facility}, {callsign}, leaving sixteen thousand climbing to flight level two three zero.` | OK (owner rulings 2 and 3; the flight level stays in digits) |
| any | `…, one zero thousand, with {atis}.` ("level" dropped) | No: P6 |
| any | `…, leaving four thousand for one one thousand.` (owner "9,800 for 12,000") | No: P6, "climbing" dropped; "for" belongs to the climb-via form |
| Quiet, Normal | `…, leaving four thousand climbing eleven thousand.` / `…, level ten thousand, …` | No: P7, group form is busy only (owner ruling 2) |
| any | `…, leaving two thousand for ten thousand, climbing to …` / `…, climbing via the {sid} to …` | No: P6, a climb-via check-in uses "for", never "to" (5-2-9.h.9) |
| any | `…, nine point eight for twelve` | No: F3 |
| any | `…, two four zero.` (FL keyword dropped) | No: F3 |
| any | `…, flight level two forty.` | No: P7, flight levels stay in separate digits (7110.65 2-4-17.b.2) |
| any | `…, leaving two thousand, climbing via the {sid} departure.` ("for one zero thousand" dropped) | No: P6 (5-2-9.h.9 example 2) |
| any | `…, climbing via the {sid} departure to one niner thousand.` (charted top added) | No: F13 |
| any | `{callsign}, level one zero thousand, with {atis}.` | No: P1 |
| any | `{facility}, {callsign}, level one zero thousand.` (ATIS dropped) | No: P9 |
| Busy | `{facility}, {callsign}, two eight right, with {atis}.` (tower case) | OK (owner ruling 1) |
| Quiet, Normal | `…, runway two eight right` → `…, two eight right` (tower case) | No: P8 |

### 1b. VFR airborne check-in (`BuildVfrAirborne`)

| Case | Canonical (spoken) |
|---|---|
| Tower, inbound | `{facility}, {callsign} five miles south at three thousand five hundred, inbound for landing, with information {atis}.` |
| Approach, inbound | `{facility}, {callsign} five miles south at three thousand five hundred, request landing, with information {atis}.` |
| Transit | `{facility}, {callsign} five miles south at three thousand five hundred, request transition.` |
| Center | `{facility}, {callsign} at five thousand five hundred, one two miles southwest of {airport}, request transition.` |
| No destination | `{facility}, {callsign} five miles south of the field, VFR northbound at four thousand five hundred.` |

| Band | Variant | Ruling |
|---|---|---|
| Quiet | `…, inbound for landing, we have information {atis}.` | OK |
| Quiet | greeting after `{facility}` | OK (J), courtesy table |
| Normal | canonical | OK |
| Normal | `{facility}, {callsign}, five miles south, three thousand five hundred, inbound for landing, with information {atis}.` | OK (4-2-3.a.3 example "Twenty Northwest Rome, seven thousand five hundred") |
| Busy | `{facility}, {callsign}, five miles south, three thousand five hundred, inbound, with {atis}.` | OK (J): to a tower "inbound" alone means to land |
| Busy | `{facility}, {callsign}, five miles south, three thousand five hundred, request transition.` | OK |
| Busy | `{facility}, {callsign} five miles south of the field, VFR northbound, four thousand five hundred.` | OK |
| Busy | `{facility}, {callsign} at eleven thousand five hundred, one two miles southwest of {airport}, request transition.` (center case at 11,500) | OK (owner ruling 2; below 10,000 ft both forms are the same words) |
| any | `…, five south at three thousand five hundred, …` | No: P7 |
| any | `…, inbound for a full stop, …` | No: P9, adds an intent the sim does not hold |
| any | `…, five miles south, VFR northbound …` ("of the field" dropped) | No: house rule in `docs/pilot-phraseology-examples.md` (a bare direction beside "northbound" reads as a contradiction) |
| any | `…, transition.` ("request" dropped) | No: P5 |

### 1c. Spawn-on-final check-in (`BuildOnFinal`)

| Case | Canonical (spoken) |
|---|---|
| IFR with approach | `{facility}, {callsign}, ILS two eight right.` |
| VFR / no approach | `{facility}, {callsign} three mile final runway two eight right, with information {atis}.` |

| Band | Variant | Ruling |
|---|---|---|
| Quiet | `{facility}, {callsign}, on the ILS two eight right.` | OK |
| Quiet | `…, three mile final runway two eight right, we have information {atis}.` | OK |
| Normal | canonical | OK |
| Normal | `{facility}, {callsign}, ILS runway two eight right.` | OK |
| Busy | canonical (IFR) | OK |
| Busy | `…, three mile final runway two eight right, with {atis}.` | OK |
| Busy | `…, three mile final two eight right, with {atis}.` | OK (owner ruling 1) |
| any | `{facility}, {callsign}, two eight right.` | No: P5, approach type dropped |
| Quiet, Normal | `…, three mile final two eight right, …` | No: P8 |

### 1d. Ground call-ups (`BuildReadyToTaxi`, `BuildPushbackRequest`, `BuildClearanceRequest`)

| Case | Canonical (spoken) |
|---|---|
| Stand | `{facility}, {callsign} at gate foxtrot eight, with information {atis}, IFR to {destination}, ready to taxi.` |
| Taxiway | `{facility}, {callsign} on taxiway kilo, with information {atis}, IFR to {destination}, ready to taxi.` |
| After push | `{facility}, {callsign} pushed back from gate foxtrot eight, with information {atis}, IFR to {destination}, ready to taxi.` |
| Taxiway bar | `{facility}, {callsign} holding short of charlie at tango four one whiskey, with information {atis}, …, ready to taxi.` |
| Arrival on a taxiway | `{facility}, {callsign} on taxiway kilo, with information {atis}, request taxi to parking.` |
| Pushback | `{facility}, {callsign} at gate foxtrot eight, with information {atis}, request pushback.` |
| Clearance, IFR | `{facility}, {callsign}, at gate foxtrot eight, with information {atis}, IFR to {destination}.` |
| Clearance, VFR | `{facility}, {callsign}, at gate foxtrot eight, with information {atis}, VFR departure to the north, at four thousand five hundred.` |

| Band | Variant | Ruling |
|---|---|---|
| Quiet | `…, IFR to {destination}, we're ready to taxi.` | OK (J) |
| Quiet | `…, with information {atis}, ready for pushback.` | OK (J): message type kept |
| Quiet | `…, IFR to {destination}, ready to copy.` (clearance) | OK (J): common US wording, adds no element |
| Normal | canonical (each case) | OK |
| Normal | `…, at gate foxtrot eight, have information {atis}, IFR to {destination}, ready to taxi.` | OK (4-1-18.a.5.b example "have information bravo") |
| Busy | `{facility}, {callsign}, gate foxtrot eight, with {atis}, IFR {destination}, ready to taxi.` | OK (4-2-3.a.1 example 2 "south ramp, I-F-R Memphis") |
| Busy | `{facility}, {callsign}, gate foxtrot eight, with {atis}, request push.` | OK (J) |
| Busy | `{facility}, {callsign}, gate foxtrot eight, with {atis}, IFR {destination}.` (clearance) | OK (4-2-3.a.1 example 2) |
| Busy | `…, VFR departure to the north, four thousand five hundred.` | OK |
| any | `…, with {atis}, ready to taxi.` (IFR/VFR and destination dropped) | No: P5 (J: they decide the runway) |
| any | `{facility}, {callsign}, ready to taxi.` | No: P3 |
| any | `…, ready to taxi with information {atis}.` | No: F10 |
| any | `…, with {atis}, push.` | No: P5 |
| any | `…, VFR north, four point five.` | No: F3 |

## 2. Ready for departure and release

| Case | Canonical (spoken) |
|---|---|
| At the bar (`BuildHoldingShortReady`) | `{facility}, {callsign} holding short runway two eight right, ready for departure.` |
| Lined up, no clearance (`BuildLinedUpReady`) | `{facility}, {callsign} runway two eight right, ready.` |
| Untowered release (`BuildReleaseRequest`) | `{facility}, {callsign}, runway two five at {airport}, ready for departure, request release.` |

| Band | Variant | Ruling |
|---|---|---|
| Quiet | `{facility}, {callsign}, holding short of runway two eight right, ready for departure.` | OK (F6 "of") |
| Quiet | `{facility}, {callsign} holding short runway two eight right, ready to go.` | OK (J) |
| Quiet | `{facility}, {callsign}, lined up runway two eight right, ready for departure.` | OK (J) |
| Quiet | `…, runway two five at {airport}, ready for departure, request IFR release.` | OK (J) |
| Normal | canonical (each case) | OK |
| Busy | `{facility}, {callsign}, holding short runway two eight right, ready.` | OK (4-3-14.b) |
| Busy | `{callsign}, runway two eight right, ready.` (lined-up reminder, a later call) | OK (P1 allows the drop; part E may shorten) |
| Busy | `{facility}, {callsign}, runway two five at {airport}, request release.` | OK (J): the request implies readiness (7110.65 4-3-4) |
| Busy | `{callsign}, two eight right, ready.` (lined-up reminder, a later call) | OK (owner ruling 1) |
| Busy | `{facility}, {callsign}, two five at {airport}, request release.` | OK (owner ruling 1; the airport keeps the position, P3) |
| any | `{facility}, {callsign}, runway two eight right, ready for departure.` (at the bar) | No: P3, reads like the lined-up call |
| any | `…, holding short two eight right, …` | No: F6 (a hold-short keeps "runway" in every band; ruling 1 does not reach it) |
| any | `…, runway two five, request release.` (airport dropped) | No: P3 |
| any | shortened callsign on the bar call or the release request | No: P2 (first call to tower / to the radar controller) |

## 3. Position and pattern reports

Later calls to the same controller: part E may shorten the callsign; the facility is not said (canonical has none).

### 3a. Pattern and final reports (`BuildTurningLegReport`, `BuildMidfieldDownwindReport`, `BuildMidfieldDownwindReminder`, `BuildMileFinalReport`)

| Case | Canonical (spoken) |
|---|---|
| Turning leg | `{callsign}, turning base runway two eight right.` |
| Midfield (armed) | `{callsign}, midfield left downwind runway two eight right.` (also `…, left downwind runway two eight right, past midfield.` / `…, abeam the numbers.`) |
| Midfield reminder | `{callsign}, midfield downwind runway two eight right.` |
| N-mile final | `{callsign}, five mile final runway two eight right.` |

| Band | Variant | Ruling |
|---|---|---|
| Quiet | `{callsign}, we're turning base runway two eight right.` (and "we're" on each case) | OK (J) |
| Quiet | `{callsign}, five mile final for runway two eight right.` | OK |
| Normal | canonical (each case) | OK |
| Normal | `{callsign}, base runway two eight right.` | OK (FIG 4-3-1 leg name, AIM 4-3-2.c) |
| Busy | canonical (each case) | OK |
| Busy | `{callsign}, base runway two eight right.` | OK |
| Busy | `{callsign}, base two eight right.` / `{callsign}, five mile final two eight right.` / `{callsign}, midfield left downwind two eight right.` | OK (owner ruling 1) |
| Quiet, Normal | `{callsign}, base two eight right.` | No: P8 |
| any | `turning base runway two eight right, {callsign}.` | No: P4 |
| any | `alright, midfield left downwind …` | No: courtesy table; never with left/right (F-file courtesy rule) |
| any | `{callsign}, left downwind runway two eight right, midfield.` | No: F10, reads as the past-midfield form |
| any | `{callsign}, midfield two eight right.` | No: P5 (side and leg dropped); P8 too in quiet and normal |
| any | `{callsign}, five out.` | No: P7, P8 |

### 3b. Fix passage (`BuildAtFixReport`)

Canonical: `{callsign}, passing {fix}.`

| Band | Variant | Ruling |
|---|---|---|
| Quiet | `{callsign}, we're passing {fix}.` | OK (J) |
| Normal | canonical | OK |
| Busy | canonical | OK |
| any | `{callsign}, {fix}.` | No: P5; a bare fix reads as a request for direct (P/CG REPORT, "passing") |

### 3c. Surface reports (`BuildClearOfRunwayText`, `BuildHoldingShortCrossing`, `BuildHoldingShortTaxi`)

| Case | Canonical (spoken) |
|---|---|
| Clear of runway | `{callsign}, clear of runway two eight right at {twy}.` |
| Bar before a crossing | `{callsign}, holding short runway two eight right.` |
| Taxiway hold short | `{callsign}, holding short of charlie at echo.` |

| Band | Variant | Ruling |
|---|---|---|
| Quiet | `{callsign}, we're clear of runway two eight right at {twy}.` | OK (J) |
| Quiet | `{callsign}, holding short of runway two eight right.` | OK (F6) |
| Normal | canonical (each case) | OK |
| Busy | canonical (each case) | OK |
| Busy | `{callsign}, clear of two eight right at {twy}.` | OK (owner ruling 1; the runway is still named) |
| any | `{callsign}, clear at {twy}.` / `{callsign}, clear of the runway.` | No: P8 (AIM 4-3-21.b: clear of a named runway; the runway is lost altogether) |
| any | `{callsign}, short of runway two eight right.` | No: F6 |
| any | `{callsign}, holding short two eight right.` | No: F6 (keeps "runway" in every band) |

### 3d. Visual acquisition (`BuildTrafficInSight`, `BuildFieldInSight`, `BuildLostSightOf*`)

| Case | Canonical (spoken) |
|---|---|
| Traffic | `{callsign}, traffic in sight.` |
| Field | `{callsign}, field in sight.` |
| Lost field | `{callsign}, lost sight of the field.` |
| Lost traffic | `{callsign}, lost sight of the traffic.` |
| Lost traffic, field held | `{callsign}, lost sight of the traffic, we still have the field in sight.` |

| Band | Variant | Ruling |
|---|---|---|
| Quiet | `{callsign}, we have the traffic in sight.` / `…, we have the field in sight.` | OK |
| Quiet | `{callsign}, we've lost sight of the field.` / `…, of the traffic.` | OK ("lost sight of" kept) |
| Normal | canonical (each case) | OK |
| Normal | `{callsign}, have the traffic.` | OK (J) |
| Busy | canonical (each case) | OK |
| Busy | `{callsign}, lost sight of the traffic, field in sight.` | OK (the Terminal's own words) |
| any | `{callsign}, got 'em.` | No: F14 |
| any | `{callsign}, lost the field.` | No: P5; "lost sight of" is the P/CG-meaningful form, not "negative contact" (`docs/pilot-phraseology.md`, AIM 5-5-11) |
| any | naming the traffic's callsign | No: house rule (`docs/pilot-phraseology.md`, AIM 5-5-10/11) |

## 4. Requests

| Case | Canonical (spoken) |
|---|---|
| Approach assignment (`BuildArrivalApproachRequest`) | `request approach assignment, {callsign}.` |
| Closed traffic (`BuildClosedTrafficRequest`) | `{facility}, {callsign}, three miles south at one thousand five hundred, request closed traffic, with information {atis}.` |
| Class B (`BuildBravoClearanceRequest`) | `{callsign}, request clearance through the bravo.` (or "into") |
| Taxi-in (`BuildTaxiInRequest`) | `{facility}, {callsign}, clear of runway two eight right at whiskey, taxi to gate two niner.` |

| Band | Variant | Ruling |
|---|---|---|
| Quiet | `{callsign}, what approach can we expect?` | OK (J): same request, callsign first (P4) |
| Quiet | `…, request closed traffic, we have information {atis}.` | OK |
| Quiet | `{callsign}, requesting clearance through the bravo.` | OK |
| Quiet | `…, clear of runway two eight right at whiskey, request taxi to gate two niner.` | OK |
| Normal | canonical (each case) | OK |
| Normal | `{facility}, {callsign}, three miles south, one thousand five hundred, request closed traffic, with information {atis}.` | OK (4-2-3.a.3 example) |
| Busy | canonical (approach, Class B) | OK |
| Busy | `…, request closed traffic, with {atis}.` | OK |
| Busy | `{facility}, {callsign}, clear of runway two eight right at whiskey, to gate two niner.` | OK (J): "to" keeps the request |
| Busy | `{facility}, {callsign}, clear of two eight right at whiskey, to gate two niner.` | OK (owner ruling 1) |
| Normal, Busy | `Ground, {callsign}, whiskey five.` (e.g. "Ground, Southwest twenty-two thirty-four, whiskey five" at OAK) | OK (owner): the exit taxiway alone, when the parking is implied: the operator's ramp at the field is known (Southwest parks at OAK Terminal 2), so "just exited the landing runway at W5, request taxi to parking" goes unsaid. Only when the runway just landed on is unambiguous and the parking is implied; otherwise the full call. Overrides P8 and the P5 request words for this call. |
| any | `{callsign}, request approach.` | No: P5, reads as a request for an approach clearance |
| any | `{callsign}, request bravo transition.` | No: P5, names a published VFR transition, a different request |
| any | `…, three south at one thousand five, …` | No: P7, F3 |
| any | `…, clear at whiskey, gate two niner.` | No: P8, the runway is lost altogether (the exit-taxiway-alone form above is the owner's exception, when the parking is implied) |
| any | shortened callsign on closed traffic or taxi-in | No: P2 (first call to tower / ground) |

What makes the parking "implied" for the owner exception above (owner ruling): either the arrival's flight-plan remark names its stand or parking area (`FlightPlanParking.Parse`, `src/Yaat.Sim/FlightPlanParking.cs`, already used by `ArrivalExitSide` to pick the exit side, e.g. SFO GC scenarios).

Or the unified per-airport sidecar names the operator's ramp: a new operator-to-ramp section beside `cargoOperators`, `gaOperators` and `parkingAreas` (`src/Yaat.Sim/Data/AirportSidecarDefinition.cs`, schema in `src/Yaat.Sim/Data/ARTCCs/README.md`), e.g. Southwest at OAK Terminal 2. With neither, the call stays full.

No builder exists for a flight-following request, an altitude-change request or a "ready for descent" call (the last is in `docs/pilot-phraseology-examples.md` as a template only), so none is catalogued. When one is built, its floor is AIM 4-2-3.a.1 example 3 and 4-2-3.a.3 (request, position, altitude).

## 5. Other proactive calls

| Case | Canonical (spoken) |
|---|---|
| S-turns (`BuildSTurnsForSpacing`) | `{callsign}, S-turning for spacing behind the traffic.` |
| Turn-out (`BuildTurningDownwindForSpacing`) | `{callsign}, turning downwind for spacing behind the traffic, request base turn.` |
| Ground follow lost (`BuildLostTrafficHolding`) | `{callsign}, lost the traffic, holding, request taxi.` |
| Follow-up of an open request (`PilotRequestTracker.TryQueueFollowUp`) | the original call's spoken text, repeated |

| Band | Variant | Ruling |
|---|---|---|
| Quiet | `{callsign}, we're S-turning for spacing behind the traffic.` | OK (AIM 4-3-5 advisory kept) |
| Quiet | `{callsign}, we're turning downwind for spacing behind the traffic, request base turn.` | OK |
| Quiet | `{callsign}, we've lost sight of the traffic, holding position, request taxi instructions.` | OK (the Terminal's words) |
| Normal | canonical (each case) | OK |
| Busy | canonical (each case) | OK |
| any | `{callsign}, S-turns.` / `{callsign}, turning out, request base.` | No: P5 |

Follow-ups: the re-ask is the original family's call and draws its variant afresh from the band at the re-ask (J). No "second call" wording; the FAA has none.

## 6. Families held canonical-only

Same spoken text in every band, number form included (no busy group-form altitudes, no busy "runway" drop); only part E's callsign form may change, on a later call to the same controller. The owner approved this list as drafted.

| Call | Builder | Why |
|---|---|---|
| Going around, with or without a spoken reason | `BuildGoingAround`, `BuildGoingAroundLostVisualReference`, `BuildGoingAroundTrafficOnRunway`, `BuildGoingAroundNotAligned` | F12; the reason is required on the air (AIM 5-5-5.a.2) |
| Aborting takeoff; stopped on the runway | `BuildRejectingTakeoffTrafficOnRunway`, `BuildStoppedOnRunway` | F12; runway blocked, the cue for an exit (AIM 4-3-18.a) |
| Approaching minimums, no landing clearance | `BuildApproachingMinimumsNoLandingClearance` | time-critical, last chance before the missed approach (J) |
| Short final, no landing clearance | `BuildShortFinalReminder` | time-critical (J); courtesy here would be wrong for the moment |
| Runway-safety holds: traffic ahead, waiting for the traffic to pass, not clear of runway | `BuildHoldingShortTrafficAhead`, `BuildHoldingShortForFollowTraffic`, `BuildHoldingShortNotClear`, `BuildHoldingPositionNotClear*` | "not clear of" / the hold reason is the whole message (AIM 4-3-21.b, 2-3-5.a); "runway" kept in busy too, ruling 1 does not reach a held family |
| Merge hold and request further taxi | `BuildHoldingShortBeforeTaxiway`, `BuildHoldingShortTaxi(requestFurtherTaxi: true)` | sim-convention wording whose locative must survive (F6) |
| Every unable call | see Scope | owner ruling |

## Notes for the implementer and the owner

- The owner's rulings as applied here: P6 now takes "climbing to" / "descending to" on an initial check-in in every band (ruling 3; readbacks still refuse "to" below FL180, so the check-in builder and the readback shortcut need separate rules). P7 lets busy speak altitudes below FL180 in group form (ruling 2), never flight levels. P8 lets busy drop "runway" with the number and L/R/C kept (ruling 1), except a hold-short report (F6) and the families held canonical-only.
- The P-rules reduce to two checks on top of the floor's token check: the canonical's facility, ATIS code word, position clause and request words appear in the variant, in order; and the callsign token sits at the same index (start or after the facility). The "runway" word is optional in that comparison for busy only.
- The generic aviation-floor check YAAT-1078 adds compares digit runs after `AtcNumberParser.NormalizeDigits`. Group-form altitudes need that check to equate "eleven thousand" with "one one thousand" ("twelve thousand" with "one two thousand", and so on to "seventeen thousand niner hundred"); the YAAT-1082 build adds that equivalence, limited to altitudes below FL180 followed by "thousand", so "flight level two forty" still fails.
- `PilotRequestTracker.TryQueueFollowUp` (`src/Yaat.Sim/Pilot/PilotRequestTracker.cs:126`) re-queues the stored `LastPilotLineTts`. To re-pick on a re-ask it has to store the canonical spoken text, not the variant spoken first.
- Found while reading, outside this catalogue (for the plan):
  - `BuildArrivalApproachRequest` puts the callsign last (`PilotResponder.cs:1336`), unlike every other pilot-initiated call; P4 suggests `{callsign}, request approach assignment.`
  - `BuildHoldingShortTaxi` speaks raw ids: its label comes from `HoldShortTarget.Describe` and the taxiway unspelled (`HoldingShortPhase.cs:75-79`, `PilotResponder.cs:1934`), so TTS says "holding short of C at E".
  - `BuildClearOfRunwayText` speaks the taxiway unspelled (`PilotResponder.cs:2014`).
  - `BuildHoldingShortNotClear` and `BuildHoldingPositionNotClear` drop "runway" in the spoken form ("not clear of two eight left", `PilotResponder.cs:2291`, `:2320`) while the Terminal keeps it.
  - The descend-via check-in names no runway transition or landing direction (`BuildViaClause`, `PilotResponder.cs:2571-2591`); AIM 5-4-1.a.2(b) asks for it when assigned. The docs cite this paragraph as "5-4-1.b.2"; the lettering is a.2(b).
  - The VFR transit check-in says only "request transition", with no route or destination (`PilotResponder.cs:2676`); AIM 4-1-18.a.5.c asks a transiting pilot for destination and/or route.
