# Readback variants by load band (YAAT-1082)

What this is: the catalogue of spoken readback bodies a solo pilot picks from in each load band, family by family, for part G of YAAT-849 (design: [pilot-phraseology-variation.md](../pilot-phraseology-variation.md)).
Status: reviewed by the owner (2026-10-23); rulings applied.
How to mark it up: strike a variant (`~~text~~`), add a row under its canonical with your band, or change the Band cell; write a ruling you disagree with as `owner: …` in its Basis cell.

## Reading the tables

- **Canonical** is today's spoken body, quoted from the code (path:line under `src/Yaat.Sim/`). Each family's first table lists them as C1, C2, …; the variant table refers to them.
- **Every variant ends ", {cs}"** unless the row shows `{cs}` somewhere else. The callsign form (N-number shortening) is part E and the frequency form is part F; this file covers body wording and, for busy altitudes only, the altitude's number form. Numbers are shown in the FAA digit form (altitudes AIM 4-2-9.a, headings AIM 4-2-10, speeds AIM 4-2-11) except the busy group-form altitude and speed rows (see "Floor as the owner's rulings change it" below).
- **Owner ruling 1, 2, 3** in a Basis cell name the owner's YAAT-1082 rulings listed in the next section.
- **Bands**: Q quiet, N normal, B busy. The pilot's trait shifts the band first; then a seeded pick chooses one row of that band per transmission. N always holds the canonical, marked `= C`.
- **Ruling**: `OK` (obeys the floor), `No: F4` (breaks that floor rule, kept to show the boundary), `No: J` (breaks no F-rule, rejected on judgement, reason in Basis).
- **Basis**: `US` = I have heard it as ordinary US pilot usage; `J` = my judgement, no FAA text decides it; otherwise the AIM / 7110.65 paragraph. Cites are lettered (AIM 4-4-7.b.1), not the md files' numbering.
- **Courtesy rules** (from the floor section of the design doc, applied everywhere below): "alright, " only in Q, never with "left"/"right" in the body, never on a runway clearance or an F12 verb; ", thanks" only in Q (rarely N), never on go around, cancel, hold position or LAHSO.

## Floor as the owner's rulings change it

The owner's YAAT-1082 rulings:

1. A busy pilot may drop the word "runway" from a runway clearance readback ("cleared to land two eight right"); quiet and normal keep it. The runway number and L/R/C always stay.
2. A busy pilot may say altitude thousands as a group ("eleven thousand", "seventeen thousand") instead of digits; quiet and normal keep the digit form.
3. Readbacks keep the earlier ruling: no "to" before an altitude below FL180 ("climbing to" / "descending to" / "up to" / "down to" only before a flight level). The initial check-in is different and lives in `pilot-calls.md`.

The floor rows these touch, restated as they now read for this catalogue (the design doc's F-table is the base; these rows override it per band):

| # | Rule as it now reads | Cite |
|---|---|---|
| F3 | Every assigned altitude or restriction keeps its keyword ("thousand" / "hundred" or "flight level") and its qualifier ("at", "at or above", "at or below", "until established", "except maintain"). Q and N speak altitudes below FL180 in separate digits ("one one thousand"). B may speak them in group form ("eleven thousand", "seventeen thousand niner hundred"; the hundreds stay a single digit plus "hundred"). Below 10,000 ft the two forms are the same words ("four thousand five hundred"). Flight levels are always "flight level" plus separate digits in every band ("flight level one niner zero", never "flight level one ninety"). No decimal shorthand, no bare FL digits, in any band. | AIM 4-4-7.b, 4-2-9.a/b; 7110.65 2-4-17.b.1 and its NOTE (group form), 2-4-17.b.2 (flight levels); owner ruling 2 |
| F5 | Every runway assignment keeps the runway number and L/R/C in every band: taxi, line-up, takeoff, landing, option / T&G / S&G / low approach, approach clearance, pattern entry with a runway. Q and N also keep the word "runway" wherever the canonical says it. B may drop the word "runway" on a runway clearance: cleared for takeoff, cleared to land, line up and wait, cleared touch and go / stop and go / low approach / option, and an approach clearance ("cleared ILS two eight right"). B keeps "runway" on everything else: the taxi destination, an "expect" approach, a pattern entry or pattern-runway clause, and the airport-level visual's "landing runway". | AIM 4-4-7.b.4, 4-3-18.a.9.a; 7110.65 2-4-3 NOTE 1.c; owner ruling 1 |
| F6, F7 | Unchanged: "hold short (of) runway {rwy}" and "cross runway {rwy}" keep "runway" in every band. Ruling 1 names clearances; a hold-short or crossing is the incursion item itself. | AIM 4-3-18.a.9.b/c, 4-3-18.b.1 |

My judgement on scope, for the owner to overrule:

- **Which readbacks count as a "runway clearance" (ruling 1)**: the F12 clearance verbs that carry a runway, plus line up and wait and approach clearances, because each puts the aircraft on or toward that runway and its readback is the runway check. Not the taxi destination ("runway three zero via sierra one, echo": with "taxi" already dropped in busy, a bare "three zero via …" loses the cue), not "expect" (no clearance yet), not a pattern entry (an instruction, not a clearance).
- **Group form never applies to flight levels**: 7110.65 2-4-17.b.1 NOTE allows group form for altitudes only, and 2-4-17.b.2 gives flight levels as separate digits with no such note; AIM 4-2-9.b agrees. I found no basis to extend ruling 2 above FL180.
- **Group form stays out of the families held canonical-only** (military routes, the crossing restriction with speed, LAHSO and the rest in the summary): those keep the canonical exactly, number form included.
- **Speeds follow altitudes**: Q and N speak separate digits plus 'knots' (AIM 4-2-11; 7110.65 2-4-17.l.1; group form is ATC's 'by N knots' form, 5-7-2.a.4); B may say 'two fifty knots' / 'speed two fifty'. `SpeedWords` speaks separate digits in every band and in the canonical; the group form is `SpeedGroupWords`, reached only by the busy `{spd:group}` rows (owner ruling).
- The variant tables show group form only where a family's example altitude is 10,000 ft or more; in every other busy row, an altitude from 10,000 to 17,900 ft may take group form under F3 as restated.

## 1. Heading and turns

| Ref | Canonical | Source |
|---|---|---|
| C1 | turn left heading two seven zero | Speech/PhraseologyRules.cs:58-59 (TL/TR) |
| C2 | turn two zero degrees left | PhraseologyRules.cs:61-62 (RELL/RELR) |
| C3 | fly heading two seven zero | PhraseologyRules.cs:63 (FH) |
| C4 | fly present heading | PhraseologyRules.cs:65 (FPH) |

| Ref | Band | Variant | Ruling | Basis |
|---|---|---|---|---|
| C1 | Q | turning left heading two seven zero | OK | US; progressive form as in AIM 4-4-7.b.1 "Climbing to …" |
| C1 | Q | left heading two seven zero, {cs}, thanks | OK | US |
| C1 | Q | alright, turning left heading two seven zero | No: J | courtesy rule: "alright" before a left/right |
| C1 | N | turn left heading two seven zero | OK | = C |
| C1 | N | left heading two seven zero | OK | US; today's shortcut |
| C1 | B | left heading two seven zero | OK | US |
| C1 | B | left two seven zero | No: F4 | drops "heading" (owner's "Left 200") |
| C1 | B | heading two seven zero | No: F4 | drops the turn direction the controller gave |
| C1 | B | two seven zero | No: F4 | bare three digits, could be a speed or FL |
| C2 | Q | turning two zero degrees left | OK | J; AIM 4-2-10 |
| C2 | N | turn two zero degrees left | OK | = C |
| C2 | N | two zero degrees left | OK | US |
| C2 | B | two zero degrees left | OK | US |
| C2 | B | two zero left | No: F4 | sounds like "runway two zero left"; today's busy shortcut, fix it |
| C3 | Q | fly heading two seven zero, {cs}, thanks | OK | US |
| C3 | N | fly heading two seven zero | OK | = C |
| C3 | N | heading two seven zero | OK | US; today's shortcut |
| C3 | B | heading two seven zero | OK | US |
| C3 | B | two seven zero | No: F4 | |
| C4 | Q | maintaining present heading | OK | J |
| C4 | N | fly present heading | OK | = C |
| C4 | B | present heading | OK | US |
| C4 | B | wilco | No: F4 | J: a vector keeps "heading" even with no digits (AIM 4-4-7.b lists vectors as readback items) |

## 2. Altitude

| Ref | Canonical | Source |
|---|---|---|
| C1 | climb and maintain one one thousand | PhraseologyRules.cs:74 (CM, below FL180; DM alike) |
| C2 | descend and maintain flight level one niner zero | PhraseologyRules.cs:79 (DM, FL; CM alike) |
| C3 | maintain at or above four thousand ("maintain VFR at or above …" when VFR) | Pilot/PilotResponder.cs:894; Pilot/PhraseologyVerbalizer.cs:149 |
| C4 | climb and maintain one one thousand, expedite climb | PilotResponder.cs:866 (EXP with altitude) |
| C5 | expedite descent | PilotResponder.cs:887 (bare EXP airborne) |
| C6 | cross sunol at or above four thousand nine hundred | PhraseologyVerbalizer.cs:163 (CFIX A/B) |
| C7 | cross sunol at and maintain one one thousand at two five zero knots | PhraseologyRules.cs:191 (CFIX alt + speed) |
| C8 | climb via sid / climb via sid except maintain five thousand | PhraseologyRules.cs:128-129 (CVIA) |
| C9 | cruise one one thousand | PhraseologyRules.cs:122 |
| C10 | depart sunol heading two seven zero | PhraseologyRules.cs:198 |

| Ref | Band | Variant | Ruling | Basis |
|---|---|---|---|---|
| C1 | Q | climbing one one thousand | OK | US; owner ruling: no "to" before a number below FL180 |
| C1 | Q | alright, we'll climb and maintain one one thousand, {cs}, thanks | OK | owner's verbose form |
| C1 | Q | climbing to one one thousand | No: J | owner ruling 3 (to/two trap below FL180; readbacks only); today's shortcut, restrict it to FLs |
| C1 | N | climb and maintain one one thousand | OK | = C |
| C1 | N | {cs}, roger, climb and maintain one one thousand | OK | AIM 4-4-7.b.1 (ID first + roger) |
| C1 | B | one one thousand | OK | US; owner's "4,500" example; keyword kept (F3) |
| C1 | B | climbing one one thousand | OK | US |
| C1 | B | up to one one thousand / up to eleven thousand | No: J | owner ruling 3; today's shortcut, restrict to FLs |
| C1 | B | eleven thousand | OK | owner ruling 2 (busy group form); 7110.65 2-4-17.b.1 NOTE |
| C1 | B | climbing eleven thousand | OK | owner ruling 2 |
| C1 | Q, N | eleven thousand / climb and maintain eleven thousand | No: F3 | group form is busy only (owner ruling 2) |
| C1 | B | one one point zero / four point five | No: F3 | decimal shorthand |
| C2 | Q | descending to flight level one niner zero | OK | AIM 4-4-7.b.1 example "Climbing to Flight Level three three zero" |
| C2 | Q | alright, we'll descend and maintain flight level one niner zero, {cs}, thanks | OK | owner's verbose form |
| C2 | N | descend and maintain flight level one niner zero | OK | = C |
| C2 | N | descending to flight level one niner zero | OK | US |
| C2 | B | down to flight level one niner zero | OK | owner ruling (FL only) |
| C2 | B | flight level one niner zero | OK | US |
| C2 | B | descending flight level one niner zero | OK | US |
| C2 | B | down to one niner zero | No: F3 | bare FL digits (AIM 4-2-9.b) |
| C2 | B | flight level one ninety | No: F3 | group form is for altitudes below FL180 only (7110.65 2-4-17.b.2; owner ruling 2 does not reach FLs) |
| C3 | Q | maintaining at or above four thousand, {cs}, thanks | OK | J; verb kept outside B; "VFR" kept when canonical has it |
| C3 | N | maintain at or above four thousand | OK | = C |
| C3 | B | at or above four thousand | OK | J; qualifier and keyword kept |
| C3 | B | above four thousand | No: F3 | qualifier changed |
| C4 | Q | climb and maintain one one thousand, expedite climb, {cs}, thanks | OK | J |
| C4 | N | climb and maintain one one thousand, expedite climb | OK | = C |
| C4 | B | climbing one one thousand, expediting | OK | J; clause order kept |
| C4 | B | climbing eleven thousand, expediting | OK | owner ruling 2 |
| C4 | B | climbing one one thousand | No: F10 | drops the expedite clause |
| C5 | Q | expediting descent | OK | US |
| C5 | N | expedite descent | OK | = C |
| C5 | N | expediting | OK | US |
| C5 | B | expediting | OK | US |
| C5 | B | wilco | OK | no number (AIM 4-2-3.c); J: low weight, the controller wants to hear "expedite" |
| C6 | Q | we'll cross sunol at or above four thousand nine hundred | OK | J |
| C6 | N | cross sunol at or above four thousand nine hundred | OK | = C |
| C6 | B | cross sunol at or above four thousand nine hundred | OK | no shorter form |
| C6 | B | sunol at or above four thousand nine hundred | No: J | without "cross" it reads as a position report |
| C6 | B | direct sunol at or above four point nine | No: F3 | owner's form; decimal altitude, verb changed |
| C7 | N | cross sunol at and maintain one one thousand at two five zero knots | OK | = C; no variant in any band, group form included (held canonical-only) |
| C7 | B | cross sunol at one one thousand, two five zero knots | No: F3 | drops "and maintain", which says what to do after the fix |
| C8 | Q | climb via the SID | OK | US |
| C8 | N | climb via sid | OK | = C |
| C8 | N | climb via the SID except maintain five thousand | OK | US |
| C8 | B | climb via the SID, except maintain five thousand | OK | US |
| C8 | B | climb via, except five thousand | No: F3 | drops "maintain" |
| C8 | B | climb via the SID to one seven thousand | No: F13 | adds a charted top the controller did not say |
| C9 | Q | cruise one one thousand, {cs}, thanks | OK | J |
| C9 | N | cruise one one thousand | OK | = C; no shorter form in any band |
| C9 | B | cruise eleven thousand | OK | owner ruling 2; "cruise" kept |
| C9 | B | one one thousand | No: J | "cruise" is the clearance (P/CG CRUISE: block from MEA up); dropping it turns it into a hard altitude |
| C10 | N | depart sunol heading two seven zero | OK | = C; only courtesy varies (Q ", thanks") |

## 3. Speed

| Ref | Canonical | Source |
|---|---|---|
| C1 | maintain two five zero knots | PhraseologyRules.cs:97 (`PhraseologyVerbalizer.SpeedWords` speaks the digits) |
| C2 | maintain two five zero knots until sunol | PhraseologyRules.cs:117 |
| C3 | maintain mach point seven eight | PhraseologyRules.cs:111 |
| C4 | resume normal speed / no speed restrictions / reduce to final approach speed | PhraseologyRules.cs:103-109 |

| Ref | Band | Variant | Ruling | Basis |
|---|---|---|---|---|
| C1 | Q, N | slowing to two five zero knots / speeding up to two five zero knots | OK | 7110.65 5-7-2.a.4 form; only when current speed is more than 10 kt from assigned (AIM 4-4-12.c), verb matches the direction, "knots" required, never with a speed modifier or "until" |
| C1 | Q | we'll maintain two five zero knots, {cs}, thanks | OK | owner's verbose form |
| C1 | N | maintain two five zero knots | OK | = C |
| C1 | N | two five zero knots | OK | US; today's shortcut |
| C1 | B | two five zero knots | OK | US |
| C1 | B | speed two five zero | OK | US; F9 keyword "speed" |
| C1 | B | two fifty knots / speed two fifty | OK | busy group form, keyword kept (F9) |
| C1 | Q, N | two fifty knots | No: J | digits outside busy (AIM 4-2-11; 7110.65 2-4-17.l.1, 5-7-2.a.4) |
| C1 | any | two fifty | No: F9 | bare number |
| C1 | any | warp speed ahead / on the brakes, two five zero knots | No: F14 | owner's playful forms, AIM 4-2-1.c |
| C2 | N | maintain two five zero knots until sunol | OK | = C |
| C2 | B | two five zero knots until sunol | OK | J |
| C2 | B | two five zero knots | No: F10 | drops the "until" clause |
| C3 | N | maintain mach point seven eight | OK | = C |
| C3 | B | mach point seven eight | OK | US |
| C3 | B | point seven eight | No: F9 | |
| C4 | Q | resuming normal speed | OK | US |
| C4 | N | resume normal speed (and the other two as written) | OK | = C |
| C4 | B | wilco (resume normal speed, reduce to final approach speed only) | OK | no number (7110.65 2-4-3 NOTE 1.a); delete speed restrictions keeps its words (J: 5-7-4.a NOTE vs 5-7-4.d) |

## 4. Direct-to and navigation

| Ref | Canonical | Source |
|---|---|---|
| C1 | proceed direct sunol | PhraseologyRules.cs:170 |
| C2 | turn left direct sunol | PhraseologyRules.cs:173-174 |
| C3 | when able direct sunol | PhraseologyRules.cs:175 |
| C4 | hold at sunol left turns / hold present position left turns | PhraseologyRules.cs:601-606 |
| C5 | cleared through bravo airspace (and the ", cleared into/through the bravo" suffix) | PhraseologyRules.cs:294; PilotResponder `WithBravoClearance` |
| — | join airway / join radial: **no readback today** | no rule for JoinAirway, JoinRadialInbound/Outbound in PhraseologyRules.cs, so `VerbalizeCore` returns null |

| Ref | Band | Variant | Ruling | Basis |
|---|---|---|---|---|
| C1 | Q | alright, direct sunol, {cs}, thanks | OK | owner's verbose form |
| C1 | Q | we'll head direct sunol | OK | owner's verbose form |
| C1 | N | proceed direct sunol | OK | = C |
| C1 | N | direct sunol | OK | US; today's shortcut |
| C1 | B | direct sunol | OK | US |
| C1 | B | direct sunol, own nav | No: F14 | owner's form; "own nav" is jargon for "resume own navigation" (AIM 4-2-1.c) |
| C2 | Q | turning left direct sunol | OK | US |
| C2 | N | turn left direct sunol | OK | = C |
| C2 | B | left direct sunol | OK | US; today's shortcut |
| C2 | B | direct sunol | No: F4 | drops the turn direction |
| C3 | N | when able direct sunol | OK | = C |
| C3 | B | direct sunol | No: J | drops the "when able" condition, which changes when to turn |
| C4 | N | hold at sunol left turns | OK | = C |
| C4 | B | hold sunol left turns | OK | J |
| C4 | B | hold at sunol | No: J | drops "left turns", the non-standard-hold element (7110.65 4-6-1) |
| C5 | all | canonical only | OK | F12: the airspace clearance is never shortened (AIM 3-2-3) |

Join airway / radial: when a readback is added, I suggest canonical "join victor two four four" and "intercept the Oakland one two zero radial" with no variants beyond courtesy until each is ruled; that is a pre-existing gap, not part of this catalogue.

## 5. Approach clearances

| Ref | Canonical | Source |
|---|---|---|
| C1 | cleared ILS runway two eight right approach | PhraseologyRules.cs:318 (CAPP; RNAV, LOC, VOR, LDA alike) |
| C2 | cleared straight in ILS runway two eight right approach | PhraseologyRules.cs:333 |
| C3 | cleared visual approach runway two eight right (+ ", left traffic", ", follow the traffic") | Commands/ApproachCommandHandler.cs:987, :1129 (handler readback) |
| C4 | cleared visual approach to Petaluma, landing runway two nine | ApproachCommandHandler (airport-level CVA) |
| C5 | expect ILS runway two eight right approach / expect visual approach runway two eight right | PhraseologyRules.cs:346; PhraseologyVerbalizer.cs:176 |
| C6 | join the ILS runway two eight right approach | PhraseologyRules.cs:343 |
| C7 | turn left heading two four zero descend and maintain three thousand cleared ILS runway two eight right approach | PhraseologyRules.cs:452 (PTAC, rule path) |
| C8 | follow the traffic | PilotResponder.cs:481 (airborne FOLLOW) |

| Ref | Band | Variant | Ruling | Basis |
|---|---|---|---|---|
| C1 | Q | cleared ILS runway two eight right approach, {cs}, thanks | OK | US |
| C1 | Q | alright, cleared ILS runway two eight right approach | No: J | courtesy rule ("right" in the body) |
| C1 | N | cleared ILS runway two eight right approach | OK | = C |
| C1 | N | {cs}, cleared ILS runway two eight right approach | OK | AIM 4-4-7.b.1 (ID first) |
| C1 | B | cleared ILS runway two eight right | OK | US; type and runway kept |
| C1 | B | cleared ILS two eight right | OK | owner ruling 1 (busy may drop "runway" on a runway clearance; number and L/R/C kept); J: an approach clearance counts as one |
| C1 | Q, N | cleared ILS two eight right | No: F5 | "runway" kept outside busy (owner ruling 1) |
| C1 | B | cleared approach / cleared ILS | No: F5 | runway number dropped |
| C2 | N | cleared straight in ILS runway two eight right approach | OK | = C |
| C2 | B | cleared straight in ILS runway two eight right | OK | J |
| C2 | B | cleared straight in ILS two eight right | OK | owner ruling 1 |
| C2 | B | cleared ILS runway two eight right | No: J | "straight in" changes the procedure flown (7110.65 4-8-1.e) |
| C3 | Q | cleared visual approach runway two eight right, {cs}, thanks | OK | US |
| C3 | N | cleared visual approach runway two eight right | OK | = C |
| C3 | B | cleared visual runway two eight right | OK | US |
| C3 | B | cleared visual two eight right | OK | owner ruling 1; any ", left traffic" / ", follow the traffic" clause still kept (F10, F12) |
| C3 | B | cleared visual runway two eight right (", follow the traffic" dropped) | No: F10 | a follow or pattern-side clause is never dropped (F12 for follow) |
| C4 | N | cleared visual approach to Petaluma, landing runway two nine | OK | = C |
| C4 | B | cleared visual to Petaluma, landing runway two nine | OK | J |
| C4 | B | cleared visual to Petaluma, landing two nine | No: F5 | J: "landing runway" is the pilot's stated pick, not part of the clearance; ruling 1 does not reach it |
| C4 | B | cleared visual approach to Petaluma | No: F5 | the landing runway is the pilot's stated pick (7110.65 7-4-3.c.4) |
| C5 | Q | we'll expect the ILS runway two eight right | OK | J |
| C5 | N | expect ILS runway two eight right approach | OK | = C |
| C5 | N | expect the ILS runway two eight right | OK | US |
| C5 | B | expect ILS runway two eight right | OK | US |
| C5 | B | expect ILS two eight right | No: F5 | J: an "expect" is not a clearance, so ruling 1 does not reach it |
| C5 | B | expect the ILS | No: F5 | |
| C6 | N | join the ILS runway two eight right approach | OK | = C |
| C6 | B | join the ILS runway two eight right | OK | J |
| C6 | B | join the ILS two eight right | No: F5 | J: a join is not the approach clearance; ruling 1 does not reach it |
| C7 | Q | canonical, {cs}, thanks | OK | J |
| C7 | N | turn left heading two four zero, descend and maintain three thousand, cleared ILS runway two eight right approach | OK | = C (commas added for pacing) |
| C7 | B | left heading two four zero, maintain three thousand, cleared ILS runway two eight right | OK | J; F3/F4/F5 kept, order kept (F10) |
| C7 | B | left heading two four zero, maintain three thousand, cleared ILS two eight right | OK | owner ruling 1 |
| C7 | B | left two four zero, three thousand, cleared ILS | No: F4, F5 | |
| C8 | Q | we'll follow the traffic | OK | J |
| C8 | N | follow the traffic | OK | = C |
| C8 | B | following the traffic | OK | US |
| C8 | B | following | No: F12 | keep "the traffic" |

Pre-existing note on C7: the PTAC readback has no "until established" (7110.65 5-9-4 example: "Turn right heading three four zero. Maintain two thousand until established on the localizer. Cleared I-L-S runway three six approach."), because the rule pattern lacks it. Not a variant question; worth an **add**.

## 6. Takeoff and landing

| Ref | Canonical | Source |
|---|---|---|
| C1 | line up and wait runway two eight right (", without delay") | PilotResponder.cs:362, :910 |
| C2 | cleared for takeoff runway two eight right | PilotResponder.cs:841 |
| C3 | cleared for takeoff runway two eight right, turn right heading three one zero, climb and maintain three thousand | PilotResponder.cs:841, :996 |
| C4 | cleared to land runway two eight right / change to runway two eight left, cleared to land | PilotResponder.cs:892 |
| C5 | cleared to land runway two eight right, hold short runway three three | PilotResponder.cs:937 (LAHSO) |
| C6 | go around / go around fly heading two seven zero / go around make left traffic | PhraseologyRules.cs:267-270 |
| C7 | cancel takeoff clearance / cancel landing clearance | PhraseologyRules.cs:251, :259 |
| C8 | cleared touch and go runway two eight right, make left traffic runway two eight left (stop and go, low approach, option alike) | PilotResponder.cs:398-418, :962 |
| C9 | exit left whiskey three / exit whiskey three / exit left | PhraseologyRules.cs:919-925 |
| C10 | exit without delay | PilotResponder.cs:816 |

| Ref | Band | Variant | Ruling | Basis |
|---|---|---|---|---|
| C1 | Q | line up and wait runway two eight right, {cs}, thanks | OK | J |
| C1 | N | line up and wait runway two eight right | OK | = C |
| C1 | N | runway two eight right, line up and wait | OK | US; 7110.65 3-9-4 order |
| C1 | B | runway two eight right, line up and wait | OK | US |
| C1 | B | two eight right, line up and wait / line up and wait two eight right | OK | owner ruling 1; J: line up and wait counts as a runway clearance |
| C1 | B | lining up runway two eight right | No: F12 | "and wait" dropped |
| C1 | B | line up and wait | No: F7 | runway dropped |
| C2 | Q | cleared for takeoff runway two eight right, {cs}, thanks | OK | US |
| C2 | Q | alright, cleared for takeoff … | No: F12 | courtesy rule |
| C2 | N | cleared for takeoff runway two eight right | OK | = C |
| C2 | N | runway two eight right, cleared for takeoff | OK | US; 7110.65 3-9-10 order |
| C2 | B | runway two eight right, cleared for takeoff | OK | US |
| C2 | B | cleared for takeoff two eight right / two eight right, cleared for takeoff | OK | owner ruling 1 |
| C2 | Q, N | cleared for takeoff two eight right | No: F5 | "runway" kept outside busy (owner ruling 1) |
| C2 | B | cleared for takeoff | No: F5 | runway dropped |
| C2 | B | rolling | No: F12 | |
| C3 | N | as canonical | OK | = C |
| C3 | B | cleared for takeoff runway two eight right, right heading three one zero, climb and maintain three thousand | OK | J |
| C3 | B | cleared for takeoff runway two eight right, right heading three one zero, three thousand | OK | J; F3 keyword kept |
| C3 | B | cleared for takeoff two eight right, right heading three one zero, three thousand | OK | owner ruling 1 |
| C4 | Q | cleared to land runway two eight right, {cs}, thanks | OK | US |
| C4 | N | cleared to land runway two eight right | OK | = C |
| C4 | N | {cs}, roger, cleared to land runway two eight right | OK | AIM 4-4-7.b.1 example, verbatim |
| C4 | N | runway two eight right, cleared to land | OK | US |
| C4 | B | runway two eight right, cleared to land | OK | US |
| C4 | B | cleared to land two eight right / two eight right, cleared to land | OK | owner ruling 1 (the owner's example) |
| C4 | Q, N | cleared to land two eight right | No: F5 | "runway" kept outside busy (owner ruling 1) |
| C4 | B | cleared to land | No: F5 | runway dropped |
| C4 (change) | all | canonical only | OK | the runway change is the point of the readback (7110.65 3-10-5) |
| C5 | all | cleared to land runway two eight right, hold short of runway three three | OK | F8; see the note below on today's missing "of" |
| C5 | N, B | wilco, cleared to land runway two eight right, hold short of runway three three | OK | AIM 4-3-11.c.2 example ("wilco, cleared to land …") |
| C5 | any | …, hold short three three / …, {cs}, thanks | No: F8 | no shortening, no courtesy |
| C6 | all | going around | OK | US; no courtesy in any band |
| C6 | N | go around | OK | = C |
| C6 | B | going around, heading two seven zero | OK | J; keyword kept (F4) |
| C6 | B | going around, left traffic | OK | J |
| C6 | any | wilco / roger | No: F12 | |
| C7 | all | canonical, or "roger, cancel takeoff clearance" | OK | AIM 4-2-3.c; no courtesy |
| C7 | all | cancelling takeoff clearance | OK | US |
| C7 | any | roger | No: F12 | |
| C8 | Q | cleared touch and go runway two eight right, make left traffic runway two eight left, {cs}, thanks | OK | J |
| C8 | N | as canonical | OK | = C |
| C8 | N | runway two eight right, cleared touch and go, make left traffic runway two eight left | OK | US; 7110.65 3-8-2 order |
| C8 | B | runway two eight right, cleared touch and go, left traffic runway two eight left | OK | J |
| C8 | B | two eight right, cleared touch and go, left traffic runway two eight left | OK | owner ruling 1 on the clearance's runway; J: the pattern clause keeps "runway" (F5 as restated) |
| C8 | B | touch and go runway two eight right | No: J | "cleared" is the clearance word; F12 spirit |
| C8 | B | cleared touch and go runway two eight right, left traffic | No: F5 | pattern runway dropped (AIM 4-4-7.b.4) |
| C9 | Q | exiting left whiskey three | OK | J |
| C9 | N | exit left whiskey three | OK | = C |
| C9 | B | left whiskey three | OK | US |
| C9 | B | left on whiskey three | OK | owner's "Right on A" |
| C9 | B | whiskey three | No: J | drops the side the controller gave; same principle as F4 |
| C10 | all | canonical only | OK | the expedite is the instruction (7110.65 3-7-2.j) |

LAHSO note: today's C5 says "hold short runway three three" (PilotResponder.cs:944, :948). F8 and AIM 4-3-11.b.7 require the words "HOLD SHORT OF". The canonical should become "…, hold short of runway three three" before any variant ships; that is a fix to the canonical, worth an **add**.

## 7. Pattern

| Ref | Canonical | Source |
|---|---|---|
| C1 | enter left downwind runway two eight left (base, crosswind alike; runway-less forms too) | PhraseologyRules.cs:527-540 |
| C2 | make straight in runway two eight left | PhraseologyRules.cs:547 |
| C3 | make left traffic runway two eight left / make left traffic | PhraseologyRules.cs:555-560 |
| C4 | turn crosswind / turn downwind / turn base | PhraseologyRules.cs:568-570 |
| C5 | extend downwind runway two eight left / extend downwind | PilotResponder.cs:725 |
| C6 | make short approach / make normal approach | PhraseologyRules.cs:575-577 |
| C7 | make left three sixty / make left two seventy / cancel the two seventy / circle the airport | PhraseologyRules.cs:578-594 |
| C8 | will report turning base / will report midfield downwind / will report three mile final / will report passing sunol | PilotResponder.cs:689 |

| Ref | Band | Variant | Ruling | Basis |
|---|---|---|---|---|
| C1 | Q | entering left downwind runway two eight left, {cs}, thanks | OK | US (the rule comment's pilot form) |
| C1 | N | enter left downwind runway two eight left | OK | = C |
| C1 | N | entering left downwind runway two eight left | OK | US |
| C1 | B | left downwind runway two eight left | OK | US |
| C1 | B | left downwind two eight left | No: F5 | J: a pattern entry is an instruction, not a runway clearance; ruling 1 does not reach it |
| C1 | B | enter downwind runway two eight left | No: J | side dropped |
| C1 (no rwy) | B | left downwind | OK | US |
| C2 | Q | making straight in runway two eight left | OK | J |
| C2 | N | make straight in runway two eight left | OK | = C |
| C2 | B | straight in runway two eight left | OK | US |
| C3 | Q | making left traffic runway two eight left | OK | J |
| C3 | N | make left traffic runway two eight left | OK | = C |
| C3 | B | left traffic runway two eight left | OK | US |
| C3 (no rwy) | B | left traffic | OK | US |
| C4 | Q | turning base | OK | US |
| C4 | N | turn base | OK | = C |
| C4 | B | turning base / wilco | OK | no number or runway (AIM 4-2-3.c) |
| C5 | Q | extending downwind runway two eight left, {cs}, thanks | OK | J |
| C5 | N | extend downwind runway two eight left | OK | = C |
| C5 | B | extending downwind runway two eight left | OK | J |
| C5 | B | extend downwind / wilco | No: F5 | drops the runway token the canonical has (generic check) |
| C5 (no rwy) | B | extending downwind / wilco | OK | no number or runway |
| C6 | Q | making short approach | OK | US |
| C6 | N | make short approach | OK | = C |
| C6 | B | short approach / wilco | OK | no number or runway |
| C7 | Q | making left three sixty | OK | J |
| C7 | N | make left three sixty | OK | = C |
| C7 | B | left three sixty | OK | US |
| C7 | B | three sixty / wilco | No: F4 | direction dropped; a wrong-way 360 in the pattern is a conflict |
| C7 (circle, cancel 270) | B | circling the airport / cancelling the two seventy | OK | J |
| C8 | Q | will report turning base, {cs}, thanks | OK | J |
| C8 | N | will report turning base | OK | = C |
| C8 | N | wilco | OK | AIM 4-2-3.c ("Wilco" = will comply) |
| C8 | B | wilco | OK | for base, crosswind, downwind, final, midfield |
| C8 (mile final) | B | wilco | No: F9 | drops "three mile"; keep canonical |
| C8 (passing fix) | B | wilco | No: J | the fix is the trigger; keep "will report passing sunol" |

## 8. Taxi, hold short, cross, pushback, follow and give way

| Ref | Canonical | Source |
|---|---|---|
| C1 | taxi to runway three zero via sierra one, echo | PhraseologyRules.cs:781 (rule path: route, no hold-short) |
| C2 | runway three zero taxi via sierra one, echo, hold short of runway three three | PhraseologyVerbalizer.cs:470-524 (direct render) |
| C3 | taxi via alpha, bravo to parking bravo one two, cross runway two eight right, hold short of runway two five left | PhraseologyVerbalizer.cs:470 |
| C4 | taxi to runway one left (no route; TAXIAUTO) | PhraseologyRules.cs:763 |
| C5 | hold short of runway two eight left / hold short of charlie / hold short of charlie at juliett | PhraseologyRules.cs:904-912 |
| C6 | cross runway two eight left / cross runway two eight left, hold short of runway two eight right / continue taxi (taxiway target) | PhraseologyRules.cs:892; PhraseologyVerbalizer `RenderCross` |
| C7 | hold position / holding position on runway three three | PhraseologyRules.cs:885; PilotResponder.cs:457 |
| C8 | resume taxi / continue taxi (RES pre-clear) | PhraseologyRules.cs:890; GroundCommandHandler.cs:5404 |
| C9 | push back, face east / push onto alpha, face east | Pilot/PushReadbackPhrases.cs:41-76 |
| C10 | follow the traffic / behind the traffic / follow the traffic, hold short of runway one right | PilotResponder.cs:470-512 |
| C11 | taxi without delay; "…, without delay" inside a taxi | PilotResponder.cs:821; PhraseologyVerbalizer.cs:509 |
| C12 | hold short of charlie, will hold on delta before golf / unable to hold short of tango, stopping / unable mike four, taxi via … | PhraseologyVerbalizer `RenderMergeHoldShort`, `RenderUnableHoldShort`; `BuildReadbackAsApplied` |

| Ref | Band | Variant | Ruling | Basis |
|---|---|---|---|---|
| C1 | Q | alright, we'll taxi to runway three zero via sierra one, echo | OK | owner's verbose form; no "alright" when the route carries "left on"/"right on" hints |
| C1 | N | taxi to runway three zero via sierra one, echo | OK | = C |
| C1 | N | runway three zero via sierra one, echo | OK | owner's shorter form |
| C1 | N | taxi via sierra one, echo to runway three zero | OK | F10 permitted TAXI exception (owner's "Less strict") |
| C1 | B | runway three zero via sierra one, echo | OK | US |
| C1 | B | runway three zero, sierra one, echo | OK | owner's "Runway 28 taxi S1, E" less "taxi" |
| C1 | B | sierra one, echo | No: F5 | destination runway dropped |
| C1 | B | three zero via sierra one, echo | No: F5 | J: the taxi destination is not a runway clearance; with "taxi" dropped, "runway" is the only cue left |
| C2 | Q | runway three zero taxi via sierra one, echo, hold short of runway three three, {cs}, thanks | OK | J; no "alright" (hold short is F12) |
| C2 | N | runway three zero taxi via sierra one, echo, hold short of runway three three | OK | = C |
| C2 | N | runway three zero via sierra one, echo, hold short of runway three three | OK | US |
| C2 | B | runway three zero via sierra one, echo, hold short runway three three | OK | US; F6 allows "hold short" without "of" |
| C2 | B | …, short runway three three | No: F6 | owner's form |
| C2 | B | runway three zero, hold short of runway three three | No: F11 | AIM 4-3-18 example's minimum, but the house rule keeps the route |
| C3 | N | as canonical | OK | = C |
| C3 | B | via alpha, bravo to parking bravo one two, cross runway two eight right, hold short of runway two five left | OK | J |
| C3 | B | …, cross two eight right, … | No: F7 | owner's "Cross 33 at B" |
| C4 | all | taxi to runway one left | OK | = C in N |
| C4 | B | taxi runway one left | OK | J |
| C4 | B | runway one left | No: J | settled in the design doc: at the bar it sounds like a line-up acknowledgement; today's busy shortcut, fix it |
| C5 | Q | will hold short of runway two eight left | OK | J |
| C5 | N | hold short of runway two eight left | OK | = C |
| C5 | N, B | hold short runway two eight left | OK | US; AIM 4-3-18.b.1 |
| C5 | B | hold short two eight left | No: F6 | "runway" kept for a runway target |
| C5 | Q | holding short of runway two eight left | No: J | reads as the arrival report, not an acknowledgement |
| C5 (taxiway) | B | hold short charlie | OK | US |
| C5 (located) | all | keep "at juliett" | OK | F6 locative |
| C6 | Q, N | wilco, cross runway two eight left | OK | AIM 4-3-11.c.2 crossing example |
| C6 | N | cross runway two eight left | OK | = C |
| C6 | B | cross runway two eight left | OK | no shorter form |
| C6 | any | crossing runway two eight left | No: J | reads as a report of what it is doing |
| C6 | any | cross two eight left | No: F7 | |
| C6 (taxiway) | B | continuing | OK | J; the release has no number or runway |
| C7 | all | canonical only, or "holding position" | OK | F12; no courtesy |
| C7 | any | wilco / roger | No: F12 | |
| C8 | Q | resuming taxi | OK | J |
| C8 | N | resume taxi | OK | = C |
| C8 | B | continuing taxi | OK | US |
| C8 (with cross/HS) | all | the cross and hold-short clauses as in C5/C6 | OK | F6, F7 |
| C9 | Q | pushing back, face east, {cs}, thanks | OK | non-movement area, no FAA readback text (7110.65 3-7-2 NOTE 2) |
| C9 | N | push back, face east | OK | = C |
| C9 | B | push back, face east | OK | no shorter form |
| C9 | B | pushing back | No: J | drops the facing; keep every taxiway and facing word |
| C10 | N | follow the traffic / behind the traffic | OK | = C |
| C10 | B | following the traffic | OK | US |
| C10 | any | following / behind | No: F12 | |
| C10 (armed) | all | canonical only | OK | the hold-short is read back (AIM 4-3-18) |
| C11 | all | canonical only | OK | 7110.65 3-7-2.j |
| C12 | all | canonical only | OK | the substitution or stop is the whole point (AIM 4-3-18.b, 4-4-7.c) |

## 9. Contact and handoff (body only)

Frequency words are shown as {freq} (part F decides "one two five point three five" vs "twenty-five thirty-five" vs "ground point niner").

| Ref | Canonical | Source |
|---|---|---|
| C1 | NorCal Approach on {freq}, {cs}, so long | PilotResponder.cs:1377 |
| C2 | NorCal Approach, {cs}, so long (no frequency given) | Commands/ContactCommandHandler.cs:134 |
| C3 | {cs}, good day (FCA) | PilotResponder.cs:1394 |

| Ref | Band | Variant | Ruling | Basis |
|---|---|---|---|---|
| C1 | Q | over to NorCal Approach on {freq}, {cs}, good day | OK | US |
| C1 | Q | we'll contact NorCal Approach on {freq}, {cs}, thanks | OK | owner's verbose form |
| C1 | N | NorCal Approach on {freq}, {cs}, so long | OK | = C |
| C1 | N | NorCal Approach, {freq} | OK | owner's "Taipei Control 124.6" |
| C1 | B | {freq} | OK | AIM 4-2-3.d.2 example ("one three four point five, United Two Twenty-Two") |
| C1 | B | approach, {freq} | OK | owner's "Control 124.6" |
| C1 | any | NorCal Approach / approach (frequency dropped) | No: F9 | read back the numbers (7110.65 2-4-3 NOTE 1.a) |
| C1 | any | …, see ya | No: F14 | |
| C2 | Q | over to NorCal Approach, {cs}, good day | OK | US |
| C2 | N | NorCal Approach, {cs}, so long | OK | = C |
| C2 | B | approach, {cs} | OK | J; there is no number to read back |
| C3 | Q | good day, {cs}, thanks | OK | J |
| C3 | N | {cs}, good day | OK | = C |
| C3 | B | {cs} / good day, {cs} | OK | AIM 4-2-3.d.1 needs only an acknowledgement |

## 10. Military routes

| Ref | Canonical | Source |
|---|---|---|
| C1 | cleared into i-r five thirty one, maintain i-r five thirty one altitudes / …, maintain at or below one five thousand | PilotResponder.cs:605 |
| C2 | cleared to conduct refueling along three twelve track, maintain block flight level two four zero through flight level two six zero | PilotResponder.cs:631 |
| C3 | maintain i-r five thirty one altitudes | PilotResponder.cs:652 |
| C4 | cleared to sunol from i-r five thirty one via victor two four four, maintain one one thousand | PilotResponder.cs:666 |

**Held canonical-only in every band, courtesy included.** Each clause is the clearance itself (7110.65 9-2-6.a/b, 9-2-13): "route altitudes", "block … through …" and "from i-r …" have no shorter form that keeps their meaning, military call signs are never abbreviated (7110.65 2-4-9.b.2(b)), and the volume is too low for variety to be heard. Only the callsign position (start or end) may vary; busy group-form altitudes do not apply here.

## 11. Families outside the requested list

Variation covers every readback, so these need rulings too; drafted briefly.

| Ref | Canonical | Band | Variant | Ruling | Basis |
|---|---|---|---|---|---|
| Squawk | squawk four five two one (PhraseologyRules.cs:636) | Q, B | squawking four five two one | OK | US |
| Squawk | | B | four five two one | No: F9 | four bare digits, easiest number to mishear |
| Squawk VFR / normal / standby | squawk VFR … | B | squawking VFR | OK | US |
| Ident | squawk ident (PhraseologyRules.cs:640) | B | ident | OK | US |
| Ident | | B | {cs} alone | OK | J; AIM 4-2-3.c, no number |

## Summary for the owner

- **Your rulings as applied**: busy drops "runway" on runway clearances only (§5 C1 to C3 and C7, §6 C1 to C4 and C8), and keeps it on the taxi destination, "expect", "join", pattern entries, the pattern clause of a touch and go and the airport-level visual's "landing runway" (my reading of "runway clearance"; F5 as restated above).

  Busy group-form altitudes are added where a family's example is 10,000 ft or more (§2 C1, C4, C9) and allowed by F3 everywhere else in busy; never on flight levels, speeds, or the families held canonical-only. Readbacks still take no "to" before an altitude below FL180.
- **Held canonical-only** (body never varies, number form included; only the callsign position): military routes (§10); LAHSO (§6 C5, "wilco, " prefix only); the Class B clearance (§4 C5); cancel takeoff / landing clearance (§6 C7, an acknowledgement word only); hold position (§8 C7); "exit / taxi without delay" (§6 C10, §8 C11).

  Also: the change-of-runway landing clearance (§6 C4); the armed follow with hold-short (§8 C10); merge-hold, unable-hold-short and taxi-as-applied readbacks (§8 C12); the crossing restriction with speed (§2 C7). Each because the whole body is the safety item.
- **Changes to today's canonical or shortcuts this catalogue asks for**: LAHSO gains "of" (F8); the relative-turn shortcut keeps "degrees"; the route-less TAXI shortcut keeps "taxi"; "climbing to / up to / down to" only for flight levels; PTAC lacks "until established" (pre-existing).
- **Pre-existing gap**: JoinAirway and JoinRadial have no readback at all.

## Notes for the implementer

- The value floor (`AviationFloor.HoldsTemplate`) compares captures, not rendered digit runs, so group and digit forms come from one number by construction and no equivalence between "eleven thousand" and "one one thousand" is needed.
- The same check fails a runway designator spoken without "runway" today. The busy band needs it to accept "two eight right" for the runway-clearance families listed under F5 above, and only there; every other family and band keeps the "runway" requirement.
